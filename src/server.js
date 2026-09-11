import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import https from 'node:https';
import { fileURLToPath } from 'node:url';
import { WebSocketServer, WebSocket } from 'ws';
import { config } from './config.js';
import { createFlightPlanCache } from './simbrief.js';
import { WEATHER_MAX_ZOOM, createTileCache, upstreamUrl, validCoordinate, weatherLayers } from './weather.js';
import { XPlaneUdpBridge } from './xplane/udpBridge.js';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const publicRoot = path.join(projectRoot, 'public');
const mime = new Map([
  ['.html', 'text/html; charset=utf-8'], ['.js', 'text/javascript; charset=utf-8'],
  ['.css', 'text/css; charset=utf-8'], ['.json', 'application/json; charset=utf-8'],
  ['.webmanifest', 'application/manifest+json'], ['.svg', 'image/svg+xml'], ['.png', 'image/png'], ['.ico', 'image/x-icon']
]);

function sendJson(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
    'Cache-Control': 'no-store'
  });
  response.end(body);
}

function serveFile(response, root, relative, cache = false) {
  const safeRelative = relative.replace(/^[/\\]+/, '');
  const target = path.resolve(root, safeRelative);
  if (target !== root && !target.startsWith(`${root}${path.sep}`)) return sendJson(response, 403, { error: 'Forbidden' });
  fs.stat(target, (error, stat) => {
    if (error || !stat.isFile()) return sendJson(response, 404, { error: 'Not found' });
    response.writeHead(200, {
      'Content-Type': mime.get(path.extname(target).toLowerCase()) || 'application/octet-stream',
      'Cache-Control': cache ? 'public, max-age=604800, immutable' : 'no-cache',
      'X-Content-Type-Options': 'nosniff',
      'Cross-Origin-Resource-Policy': 'same-origin'
    });
    fs.createReadStream(target).pipe(response);
  });
}

const bridge = new XPlaneUdpBridge({ host: config.udpHost, port: config.udpPort, sourceIp: config.sourceIp });
const flightPlans = createFlightPlanCache({
  user: config.simbriefUser,
  endpoint: config.simbriefEndpoint || undefined
});
const weatherTiles = createTileCache();
const appConfig = {
  websocketPath: '/ws',
  customBaseMap: config.customBaseMapUrl ? {
    name: config.customBaseMapName || '自定义底图', url: config.customBaseMapUrl,
    attribution: config.customBaseMapAttribution || 'Custom map provider'
  } : null,
  // The API key itself is never sent to the browser.
  weather: { configured: config.weatherApiKey.length > 0, maxZoom: WEATHER_MAX_ZOOM, layers: weatherLayers() },
  demo: config.demo
};

const handler = async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  if (request.method !== 'GET' && request.method !== 'HEAD') return sendJson(response, 405, { error: 'Method not allowed' });
  if (url.pathname === '/api/config') return sendJson(response, 200, appConfig);
  if (url.pathname.startsWith('/api/weather/tile/')) return serveWeatherTile(request, response, url);
  if (url.pathname === '/api/weather/status') return sendJson(response, 200, { layers: weatherTiles.status() });
  if (url.pathname === '/api/flightplan') {
    const select = url.searchParams.get('select');
    if (select && !flightPlans.select(select)) return sendJson(response, 404, { error: '找不到这份历史计划' });
    const refresh = url.searchParams.get('refresh') === '1';
    return sendJson(response, 200, await flightPlans.get({ refresh }));
  }
  if (url.pathname === '/api/flightplan/history') return sendJson(response, 200, {
    configured: flightPlans.status().configured,
    activeId: flightPlans.status().activeId,
    entries: flightPlans.history()
  });
  if (url.pathname === '/api/flightplan/ofp') return sendJson(response, 200, flightPlans.ofp());
  if (url.pathname === '/api/status') return sendJson(response, 200, {
    udp: { host: config.udpHost, port: config.udpPort, sourceIp: config.sourceIp || null },
    stats: bridge.stats,
    hasPosition: Number.isFinite(bridge.state.latitude) && Number.isFinite(bridge.state.longitude),
    demo: config.demo,
    simbrief: flightPlans.status().configured
  });
  const relative = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.slice(1));
  return serveFile(response, publicRoot, relative, url.pathname.startsWith('/vendor/'));
};

async function serveWeatherTile(request, response, url) {
  const [, , , , layer, zs, xs, ys] = url.pathname.split('/');
  // Leaflet templates end with ".png", so the last coordinate is "7.png".
  const tileNumber = (text) => {
    const value = Number(String(text ?? '').split('.')[0]);
    return Number.isInteger(value) ? value : Number.NaN;
  };
  const z = tileNumber(zs);
  const x = tileNumber(xs);
  const y = tileNumber(ys);
  const fail = (status, message) => sendJson(response, status, { error: message });
  if (!validCoordinate(layer, z, x, y)) return fail(400, '瓦片参数不合法');
  if (config.weatherApiKey.length === 0) return fail(404, '未配置 OpenWeatherMap API Key');

  const key = `${layer}/${z}/${x}/${y}`;
  const cached = weatherTiles.get(key);
  if (cached) {
    response.writeHead(200, { 'Content-Type': cached.contentType, 'Cache-Control': 'public, max-age=300', 'X-Content-Type-Options': 'nosniff' });
    return response.end(cached.body);
  }
  if (!weatherTiles.allow()) return fail(429, '天气瓦片请求过于频繁，请稍后再刷新');

  try {
    const upstream = await fetch(upstreamUrl(layer, config.weatherApiKey, z, x, y));
    const body = Buffer.from(await upstream.arrayBuffer());
    if (upstream.status === 401 || upstream.status === 403) return fail(401, 'OpenWeatherMap 拒绝了该 API Key');
    if (!upstream.ok) return fail(502, `OpenWeatherMap 返回 HTTP ${upstream.status}`);
    const contentType = upstream.headers.get('content-type') ?? 'image/png';
    weatherTiles.record(layer, body.length);
    weatherTiles.set(key, body, contentType);
    response.writeHead(200, { 'Content-Type': contentType, 'Cache-Control': 'public, max-age=300', 'X-Content-Type-Options': 'nosniff' });
    return response.end(body);
  } catch (error) {
    return fail(502, `无法连接 OpenWeatherMap：${error.message}`);
  }
}

const tlsEnabled = Boolean(config.tlsCert && config.tlsKey);
if (Boolean(config.tlsCert) !== Boolean(config.tlsKey)) throw new Error('EFB_TLS_CERT and EFB_TLS_KEY must be set together');
const server = tlsEnabled
  ? https.createServer({ cert: fs.readFileSync(config.tlsCert), key: fs.readFileSync(config.tlsKey) }, handler)
  : http.createServer(handler);

const wss = new WebSocketServer({ noServer: true });
server.on('upgrade', (request, socket, head) => {
  const url = new URL(request.url, 'http://localhost');
  if (url.pathname !== '/ws') return socket.destroy();
  wss.handleUpgrade(request, socket, head, (ws) => wss.emit('connection', ws, request));
});

function broadcast(type, payload) {
  const message = JSON.stringify({ type, payload });
  for (const client of wss.clients) if (client.readyState === WebSocket.OPEN) client.send(message);
}

wss.on('connection', (ws) => {
  ws.send(JSON.stringify({ type: 'hello', payload: { demo: config.demo, udpPort: config.udpPort } }));
  if (Object.keys(bridge.state).length) ws.send(JSON.stringify({ type: 'telemetry', payload: bridge.state }));
});
bridge.on('telemetry', (telemetry) => broadcast('telemetry', telemetry));
bridge.on('listening', (address) => console.log(`UDP listening on ${address.address}:${address.port}`));
bridge.on('error', (error) => console.error(`UDP error: ${error.message}`));
bridge.start();

let demoTimer;
if (config.demo) {
  const started = Date.now();
  demoTimer = setInterval(() => {
    const t = (Date.now() - started) / 1000;
    const angle = t / 35;
    bridge.state = {
      latitude: 31.1434 + Math.sin(angle) * 0.06,
      longitude: 121.8052 + Math.cos(angle) * 0.07,
      altitudeMslFt: 6200 + Math.sin(t / 9) * 250,
      altitudeAglFt: 6000 + Math.sin(t / 9) * 250,
      indicatedAirspeedKt: 142,
      trueAirspeedKt: 151,
      groundSpeedKt: 148,
      verticalSpeedFpm: Math.cos(t / 9) * 180,
      headingTrueDeg: (360 - angle * 180 / Math.PI) % 360,
      headingMagDeg: (354 - angle * 180 / Math.PI) % 360,
      pitchDeg: 2 + Math.sin(t / 3), rollDeg: -8 * Math.cos(angle),
      source: 'demo', protocol: 'DEMO', receivedAt: Date.now()
    };
    broadcast('telemetry', bridge.state);
  }, 250);
}

server.listen(config.webPort, config.webHost, () => {
  console.log(`EFB ready at ${tlsEnabled ? 'https' : 'http'}://${config.webHost}:${config.webPort}`);
  if (!tlsEnabled) console.log('PWA service worker requires HTTPS when opened from another device. The EFB itself still works over HTTP.');
  if (config.demo) console.log('Demo telemetry enabled.');
});

function shutdown() {
  clearInterval(demoTimer);
  bridge.stop();
  wss.close();
  server.close(() => process.exit(0));
}
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
