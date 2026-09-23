import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import https from 'node:https';
import { fileURLToPath } from 'node:url';
import { WebSocketServer, WebSocket } from 'ws';
import { config } from './config.js';
import { createFlightPlanCache } from './simbrief.js';
import { WEATHER_MAX_ZOOM, createTileCache, upstreamUrl, validCoordinate, weatherLayers } from './weather.js';
import { createTswDemo } from './tswDemo.js';
import { createTrackStore } from './track.js';
import { listManuals, manualFile, parseRange } from './manuals.js';
import { XPlaneUdpBridge } from './xplane/udpBridge.js';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const publicRoot = path.join(projectRoot, 'public');
const mime = new Map([
  ['.html', 'text/html; charset=utf-8'], ['.js', 'text/javascript; charset=utf-8'],
  // ES modules are fetched with strict MIME checking: without .mjs the PDF.js
  // bundle would be refused and the whole page script graph would fail.
  ['.mjs', 'text/javascript; charset=utf-8'],
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
// The bridge records the flown track; the browser only reads it back. The dev
// server keeps its copy in the OS temp directory so a restart during a session
// behaves like the exe's %LOCALAPPDATA% file.
const flownTrack = createTrackStore({
  savePath: process.env.EFB_TRACK_FILE || path.join(os.tmpdir(), 'xplane-efb-track.json')
});
const appConfig = {
  websocketPath: '/ws',
  customBaseMap: config.customBaseMapUrl ? {
    name: config.customBaseMapName || '自定义底图', url: config.customBaseMapUrl,
    attribution: config.customBaseMapAttribution || 'Custom map provider'
  } : null,
  // The manual folder path is shown in the settings page so the pilot can see
  // which folder the reader is pointed at.
  manuals: { configured: config.manualFolder.length > 0, folder: config.manualFolder },
  // The API key itself is never sent to the browser.
  weather: { configured: config.weatherApiKey.length > 0, maxZoom: WEATHER_MAX_ZOOM, layers: weatherLayers() },
  // Which simulator drives this page. The exe keeps this in bridge-config.json; the dev server
  // only ever uses it to shape the demo data.
  telemetrySource: config.demoSource,
  demo: config.demo
};

const handler = async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  // The flown track is recorded by the bridge; the page only reads it, so it is
  // the one endpoint that also accepts DELETE (the "清轨迹" button).
  if (url.pathname === '/api/track') {
    if (request.method === 'DELETE') {
      const removed = flownTrack.clear();
      return sendJson(response, 200, { ok: true, removed });
    }
    if (request.method !== 'GET' && request.method !== 'HEAD') return sendJson(response, 405, { error: 'Method not allowed' });
    const since = Number(url.searchParams.get('since'));
    return sendJson(response, 200, flownTrack.read({
      since: Number.isFinite(since) ? since : null
    }));
  }
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
  // Manual library. The PDFs live on this machine, so the bridge lists them and
  // streams them back; the reader on the iPad never sees a filesystem.
  if (url.pathname === '/api/manuals') return sendJson(response, 200, listManuals(config.manualFolder));
  if (url.pathname === '/api/manuals/file') {
    const found = manualFile(config.manualFolder, url.searchParams.get('path') ?? '');
    if (!found) return sendJson(response, 404, { error: '找不到该手册' });
    const name = path.basename(found.absolute);
    const headers = {
      'Content-Type': 'application/pdf',
      // PDF.js asks for byte ranges so a 200 MB manual starts on page one
      // instead of after a full download.
      'Accept-Ranges': 'bytes',
      'Cache-Control': 'private, max-age=0, must-revalidate',
      'Content-Disposition': `inline; filename*=UTF-8''${encodeURIComponent(name)}`,
      'X-Content-Type-Options': 'nosniff'
    };
    const range = parseRange(request.headers.range, found.size);
    if (range) {
      response.writeHead(206, {
        ...headers,
        'Content-Range': `bytes ${range.start}-${range.end}/${found.size}`,
        'Content-Length': range.length
      });
      return fs.createReadStream(found.absolute, { start: range.start, end: range.end }).pipe(response);
    }
    response.writeHead(200, { ...headers, 'Content-Length': found.size });
    return fs.createReadStream(found.absolute).pipe(response);
  }
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
  ws.send(JSON.stringify({ type: 'hello', payload: { demo: config.demo, udpPort: config.udpPort, source: config.demo ? config.demoSource : 'xp' } }));
  if (Object.keys(bridge.state).length) ws.send(JSON.stringify({ type: 'telemetry', payload: bridge.state }));
});
// Every frame that carries a position lengthens the recorded track, whatever the
// browser is doing. This is the fix for tracks that lost the middle when Safari
// was backgrounded.
function publishTelemetry(telemetry) {
  if (Number.isFinite(telemetry?.latitude) && Number.isFinite(telemetry?.longitude)) {
    flownTrack.record([telemetry.latitude, telemetry.longitude], telemetry.receivedAt ?? Date.now());
  }
  broadcast('telemetry', telemetry);
}
flownTrack.load();
const trackSaveTimer = setInterval(() => flownTrack.save(), 15000);
trackSaveTimer.unref?.();
bridge.on('telemetry', publishTelemetry);
bridge.on('listening', (address) => console.log(`UDP listening on ${address.address}:${address.port}`));
bridge.on('error', (error) => console.error(`UDP error: ${error.message}`));
bridge.start();

let demoTimer;
if (config.demo) {
  const started = Date.now();
  // Train-shaped demo frames, so the TSW front end can be developed on a machine without the game.
  const train = config.demoSource === 'tsw' ? createTswDemo() : null;
  let lastTick = Date.now();
  demoTimer = setInterval(() => {
    if (train) {
      const now = Date.now();
      const frame = train.frame(now - lastTick);
      lastTick = now;
      if (frame) {
        // The bridge merges into a snapshot; the demo replaces it, which is equivalent because it
        // is the only producer here.
        bridge.state = frame;
        publishTelemetry(frame);
      }
      return;
    }
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
    publishTelemetry(bridge.state);
  }, 250);
}

server.listen(config.webPort, config.webHost, () => {
  console.log(`EFB ready at ${tlsEnabled ? 'https' : 'http'}://${config.webHost}:${config.webPort}`);
  if (!tlsEnabled) console.log('PWA service worker requires HTTPS when opened from another device. The EFB itself still works over HTTP.');
  if (config.demo) console.log('Demo telemetry enabled.');
});

function shutdown() {
  clearInterval(demoTimer);
  clearInterval(trackSaveTimer);
  flownTrack.save(true);
  bridge.stop();
  wss.close();
  server.close(() => process.exit(0));
}
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
