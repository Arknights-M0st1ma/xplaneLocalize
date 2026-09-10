const $ = (selector) => document.querySelector(selector);

// leaflet-rotate 0.2.4 expects Renderer.onAdd(map), while Leaflet 1.9 may call
// it without that argument. The layer already owns the map at this point.
const rotateRendererOnAdd = L.Renderer.prototype.onAdd;
L.Renderer.prototype.onAdd = function onAddWithMap(mapInstance) {
  return rotateRendererOnAdd.call(this, mapInstance || this._map);
};

const map = L.map('map', {
  zoomControl: false, preferCanvas: true, tap: true,
  rotate: true, bearing: 0, dragRotate: false, touchRotate: false,
  shiftKeyRotate: false, preventPageGestures: true
}).setView([31.1434, 121.8052], 8);
L.control.zoom({ position: 'bottomleft' }).addTo(map);

const osm = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
  maxZoom: 19, attribution: '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
}).addTo(map);
let authorizedLayer = null;
let customBaseMap = null;
const baseLayers = new Map([['osm', osm]]);
let activeBaseLayer = osm;

const aircraftHtml = `<div class="aircraft-marker"><svg viewBox="0 0 64 64" aria-hidden="true"><path d="M32 3 38 27 59 36 57 42 37 38 35 58 29 58 27 38 7 42 5 36 26 27Z" fill="#f4b860" stroke="#07131b" stroke-width="2.2"/></svg></div>`;
const aircraft = L.marker([0, 0], {
  icon: L.divIcon({ className: 'aircraft-wrapper', html: aircraftHtml, iconSize: [42, 42], iconAnchor: [21, 21] }),
  interactive: false, rotation: 0, rotateWithView: true
});
const track = L.polyline([], { color: '#55d6be', weight: 3, opacity: .84, smoothFactor: 1.5 }).addTo(map);
let follow = true;
let headingUp = false;
let firstPosition = true;
let lastTelemetry = null;
let lastTrackPoint = null;
let socket;
let reconnectTimer;

function format(value, digits = 0) {
  return Number.isFinite(value) ? Math.round(value).toLocaleString('en-US', { maximumFractionDigits: digits }) : '—';
}

function setStatus(kind, text) {
  $('#status').className = `status ${kind}`;
  $('#statusText').textContent = text;
}

function distanceMeters(a, b) {
  const rad = Math.PI / 180;
  const dLat = (b[0] - a[0]) * rad;
  const dLon = (b[1] - a[1]) * rad;
  const x = dLon * Math.cos((a[0] + b[0]) * rad / 2);
  return Math.sqrt(dLat * dLat + x * x) * 6371000;
}

function updateTelemetry(data) {
  lastTelemetry = data;
  $('#gs').textContent = format(data.groundSpeedKt ?? data.trueAirspeedKt ?? data.indicatedAirspeedKt);
  $('#alt').textContent = format(data.altitudeMslFt);
  $('#hdg').textContent = Number.isFinite(data.headingTrueDeg) ? String(Math.round((data.headingTrueDeg + 360) % 360)).padStart(3, '0') : '—';
  $('#vs').textContent = format(data.verticalSpeedFpm);
  $('#sourceDiagnostic').textContent = `${data.source || '—'} · ${data.protocol || '—'}`;
  setStatus('live', data.protocol === 'DEMO' ? '演示数据' : '数据实时');

  if (!Number.isFinite(data.latitude) || !Number.isFinite(data.longitude)) return;
  const position = [data.latitude, data.longitude];
  aircraft.setLatLng(position);
  if (!map.hasLayer(aircraft) && $('#aircraftToggle').checked) aircraft.addTo(map);
  const rotation = Number.isFinite(data.headingTrueDeg) ? data.headingTrueDeg : 0;
  aircraft.options.rotation = rotation;
  aircraft.update();
  if (headingUp && Number.isFinite(data.headingTrueDeg)) {
    map.setHeading(data.headingTrueDeg, { ease: .55, deadzone: .2 });
  }
  if (!lastTrackPoint || distanceMeters(lastTrackPoint, position) > 12) {
    const points = track.getLatLngs();
    points.push(position);
    if (points.length > 2000) points.splice(0, points.length - 2000);
    track.setLatLngs(points);
    lastTrackPoint = position;
  }
  if (firstPosition) {
    map.setView(position, 12, { animate: false });
    firstPosition = false;
  } else if (follow) map.panTo(position, { animate: true, duration: .25, noMoveStart: true });
}

function connect() {
  clearTimeout(reconnectTimer);
  const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  socket = new WebSocket(`${protocol}//${location.host}/ws`);
  $('#wsDiagnostic').textContent = '连接中';
  socket.addEventListener('open', () => { $('#wsDiagnostic').textContent = '已连接'; if (!lastTelemetry) setStatus('waiting', '等待 UDP'); });
  socket.addEventListener('message', (event) => {
    try {
      const message = JSON.parse(event.data);
      if (message.type === 'telemetry') updateTelemetry(message.payload);
    } catch { showToast('收到无法解析的数据'); }
  });
  socket.addEventListener('close', () => {
    $('#wsDiagnostic').textContent = '已断开，重连中';
    setStatus('lost', '桥接断开');
    reconnectTimer = setTimeout(connect, 1500);
  });
  socket.addEventListener('error', () => socket.close());
}

function setFollow(enabled) {
  follow = enabled;
  $('#followButton').classList.toggle('active', follow);
  $('#followButton').setAttribute('aria-pressed', String(follow));
  if (follow && aircraft.getLatLng().lat) map.panTo(aircraft.getLatLng());
}

function setHeadingUp(enabled) {
  if (enabled && !Number.isFinite(lastTelemetry?.headingTrueDeg)) {
    showToast('收到有效航向数据后才能启用');
    return;
  }
  headingUp = enabled;
  const button = $('#headingUpButton');
  button.classList.toggle('active', headingUp);
  button.setAttribute('aria-pressed', String(headingUp));
  if (headingUp) {
    setFollow(true);
    map.setHeading(lastTelemetry.headingTrueDeg, { ease: .55, deadzone: .2 });
    showToast('已切换为航向向上');
  } else {
    map.setHeading(null);
    map.setBearing(0);
    const resetCenter = follow && aircraft.getLatLng().lat ? aircraft.getLatLng() : map.getCenter();
    map.setView(resetCenter, map.getZoom(), { animate: false });
    aircraft.update();
    showToast('已切换为北向上');
  }
}

function switchBaseLayer(name) {
  const next = baseLayers.get(name);
  if (!next) return;
  if (activeBaseLayer && map.hasLayer(activeBaseLayer)) map.removeLayer(activeBaseLayer);
  next.addTo(map);
  activeBaseLayer = next;
}

function openDrawer(open) {
  $('#drawer').classList.toggle('open', open);
  $('#scrim').classList.toggle('open', open);
  $('#drawer').setAttribute('aria-hidden', String(!open));
}

let toastTimer;
function showToast(message) {
  $('#toast').textContent = message;
  $('#toast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('#toast').classList.remove('show'), 2200);
}

$('#menuButton').addEventListener('click', () => openDrawer(true));
$('#closeButton').addEventListener('click', () => openDrawer(false));
$('#scrim').addEventListener('click', () => openDrawer(false));
$('#followButton').addEventListener('click', () => setFollow(!follow));
$('#headingUpButton').addEventListener('click', () => setHeadingUp(!headingUp));
$('#clearTrackButton').addEventListener('click', () => { track.setLatLngs([]); lastTrackPoint = null; showToast('轨迹已清除'); });
$('#trackToggle').addEventListener('change', (event) => event.target.checked ? track.addTo(map) : track.removeFrom(map));
$('#aircraftToggle').addEventListener('change', (event) => event.target.checked ? aircraft.addTo(map) : aircraft.removeFrom(map));
map.on('dragstart', () => setFollow(false));

document.querySelectorAll('input[name="base"]').forEach((input) => input.addEventListener('change', (event) => switchBaseLayer(event.target.value)));

async function loadConfig() {
  const response = await fetch('/api/config');
  if (!response.ok) throw new Error(`Config HTTP ${response.status}`);
  return response.json();
}

async function bootstrap() {
  const config = await loadConfig();
  $('#navigraphLink').href = config.navigraphExternalUrl;
  if (config.authorizedChart) {
    authorizedLayer = L.tileLayer(config.authorizedChart.url, { maxZoom: 19, attribution: config.authorizedChart.attribution });
    baseLayers.set('authorized', authorizedLayer);
    $('#authorizedLayerName').textContent = config.authorizedChart.name;
    $('#authorizedLayerRow').classList.remove('hidden');
  }
  if (config.customBaseMap) {
    customBaseMap = L.tileLayer(config.customBaseMap.url, { maxZoom: 19, attribution: config.customBaseMap.attribution });
    baseLayers.set('custom', customBaseMap);
    $('#customBaseMapName').textContent = config.customBaseMap.name;
    $('#customBaseMapRow').classList.remove('hidden');
  }
  connect();
}

setInterval(() => {
  if (!lastTelemetry) return;
  const seconds = Math.max(0, Math.floor((Date.now() - lastTelemetry.receivedAt) / 1000));
  $('#ageDiagnostic').textContent = `${seconds} 秒前`;
  if (seconds > 3 && socket?.readyState === WebSocket.OPEN) setStatus('lost', 'UDP 数据超时');
}, 1000);

if ('serviceWorker' in navigator && window.isSecureContext) navigator.serviceWorker.register('/sw.js').catch(() => {});
bootstrap().catch(() => showToast('无法读取应用配置'));
