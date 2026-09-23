import { TRACK_OPTIONS, pruneTrackPoints } from '/track-retention.js';
import { nmBetween, metresBetween } from '/geo.js';
import { initPages, showPage, setPlan, setSettingsInfo, currentPage } from '/pages.js';
import { initManual, refreshManuals, manualInfo, openManual } from '/manual.js';
import {
  SOURCE_TSW, TSW_PROTOCOL, formatMetres, instrumentLabels, panelVisibility,
  primarySpeed, secondaryReadouts, sourceInfo, statusText, trainPhase
} from '/protocol.js';

const $ = (selector) => document.querySelector(selector);
const RAD = Math.PI / 180;
const wrap360 = (value) => ((value % 360) + 360) % 360;
// Modulo-safe shortest turn, so a heading that has turned several times still
// steps through the smallest angle instead of spinning the long way round.
const shortestDelta = (from, to) => ((((to - from) % 360) + 540) % 360) - 180;
// Route identifiers come from a remote payload; never let them inject markup.
const escapeHtml = (value) => String(value ?? '').replace(/[&<>"']/g, (character) => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
})[character]);

// ---------------------------------------------------------------- map + rotation
// Heading-up rotates the whole Leaflet container with one CSS transform instead
// of rotating the map panes. Rotating the panes breaks Leaflet's pixel origin
// bookkeeping (the map pane offset grows without bound, tiles stop painting and
// the marker flies off screen), so nothing inside Leaflet is transformed here:
// only the wrapper is, and pointer coordinates are converted back on the way in.
const rotator = $('#mapRotator');
const stage = document.getElementById('stage');
const map = L.map('map', {
  zoomControl: false, attributionControl: false, preferCanvas: true, tap: true
}).setView([31.1434, 121.8052], 8);

const rotation = { current: 0, target: 0, heading: null, frame: null };

function centerToContainer(clientX, clientY) {
  const size = map.getSize();
  // #stage (never rotated) is the reference: the sidebar can shrink it, so the
  // viewport centre is not the map centre any more.
  const rect = stage.getBoundingClientRect();
  const dx = clientX - (rect.left + rect.width / 2);
  const dy = clientY - (rect.top + rect.height / 2);
  const angle = -rotation.current * RAD;
  const cos = Math.cos(angle);
  const sin = Math.sin(angle);
  return L.point(dx * cos - dy * sin + size.x / 2, dx * sin + dy * cos + size.y / 2);
}
map.mouseEventToContainerPoint = (event) => centerToContainer(event.clientX, event.clientY);

function applyRotation() {
  const value = rotation.current;
  rotator.style.transform = Math.abs(value) < 0.01 ? '' : `rotate(${value.toFixed(3)}deg)`;
  document.documentElement.style.setProperty('--map-rotation', `${value.toFixed(3)}deg`);
}

function normaliseRotation() {
  const turns = Math.round(rotation.current / 360);
  rotation.current -= turns * 360;
  rotation.target -= turns * 360;
}

function stepRotation() {
  rotation.frame = null;
  const difference = rotation.target - rotation.current;
  const before = Math.round(rotation.current / 10);
  rotation.current = Math.abs(difference) < 0.03 ? rotation.target : rotation.current + difference * 0.32;
  normaliseRotation();
  applyRotation();
  // The label spacing is computed in the unrotated frame, so re-run it when the
  // map has turned noticeably.
  if (Math.round(rotation.current / 10) !== before) scheduleWaypointLabels();
  if (rotation.current !== rotation.target) rotation.frame = requestAnimationFrame(stepRotation);
}

function scheduleRotation() {
  if (rotation.frame === null) rotation.frame = requestAnimationFrame(stepRotation);
}

function layoutRotator() {
  const diagonal = Math.ceil(Math.hypot(stage.clientWidth, stage.clientHeight)) + 8;
  rotator.style.setProperty('--rotator-size', `${diagonal}px`);
}

function applyAircraftRotation() {
  const element = aircraft.getElement()?.firstElementChild;
  if (!element || rotation.heading === null) return;
  element.style.transform = `rotate(${wrap360(rotation.heading).toFixed(2)}deg)`;
}

function updateHeading(value) {
  if (!Number.isFinite(value)) return;
  rotation.heading = rotation.heading === null ? value : rotation.heading + shortestDelta(rotation.heading, value);
  const turns = Math.round(rotation.heading / 360);
  if (turns) {
    rotation.heading -= turns * 360;
    rotation.target -= turns * 360;
  }
  applyAircraftRotation();
  if (headingUp) {
    rotation.target = -rotation.heading;
    scheduleRotation();
  }
}

// ---------------------------------------------------------------- base layers
const osm = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
  maxZoom: 19, attribution: '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
}).addTo(map);
const baseLayers = new Map([['osm', osm]]);
let activeBaseLayer = osm;
let weatherLayer = null;
let weatherConfig = null;
let groundConfig = null;

// ------------------------------------------------------------- map palette
// The basemap is either the light airline chart (day) or the filtered dark one
// (night mode), so every overlay takes its colours from here. Leaflet wants
// literal colours, so a theme switch re-styles the layers rather than re-reading
// CSS.
const MAP_PALETTE = {
  day: {
    track: { glow: '#0b5fa5', casing: '#ffffff', core: '#0b5fa5' },
    trackStart: { ring: '#ffffff', fill: '#0b5fa5' },
    route: { glow: '#0b5fa5', casing: '#ffffff', core: '#0b5fa5' },
    waypoint: {
      wpt: { ring: '#ffffff', fill: '#0b5fa5' },
      vor: { ring: '#ffffff', fill: '#0e8a6a' },
      ndb: { ring: '#ffffff', fill: '#b3541e' }
    },
    airport: { origin: '#0e8a6a', destination: '#0b5fa5', alternate: '#b3541e', ring: '#5d7285', fill: '#ffffff' },
    ground: {
      pavement: { edge: '#5d87a8', fill: '#cfe0ee', fillOpacity: 0.55 },
      route: '#93a8ba',
      marking: '#e0a92c',
      runwayCase: '#79899a', runway: '#a7b4bf', runwayMark: '#ffffff',
      stand: '#ffc94d', standRing: '#3d4a56'
    }
  },
  night: {
    track: { glow: '#55d6be', casing: '#03161e', core: '#7ff0d6' },
    trackStart: { ring: '#eaf2f4', fill: '#55d6be' },
    route: { glow: '#c77dff', casing: '#12071f', core: '#d8b4fe' },
    waypoint: {
      wpt: { ring: '#180d29', fill: '#e6d4ff' },
      vor: { ring: '#180d29', fill: '#7dd3fc' },
      ndb: { ring: '#180d29', fill: '#fca5a5' }
    },
    airport: { origin: '#f4b860', destination: '#f4b860', alternate: '#fca5a5', ring: '#f4b860', fill: '#08131c' },
    ground: {
      pavement: { edge: '#3fb2ff', fill: '#2f6f9a', fillOpacity: 0.42 },
      route: '#8fd6ff',
      marking: '#ffd34d',
      runwayCase: '#1b2530', runway: '#59636f', runwayMark: '#ffffff',
      stand: '#ffd34d', standRing: '#1b2530'
    }
  }
};
let mapPalette = MAP_PALETTE.day;

// ---------------------------------------------------------------- aircraft + track
// Three interchangeable inline SVGs (no extra requests, crisp at every zoom).
// They are drawn nose-up and rotated with a CSS transform, so the rotation
// centre is the icon centre and stays exact whatever the map is doing.
const AIRCRAFT_ICONS = {
  modern: `<svg viewBox="0 0 64 64" aria-hidden="true">
      <path d="M32 5.4c2.3 0 3.9 1.9 4.1 4.9l.7 11 16.9 9.1c.9.5 1.5 1.5 1.5 2.5v2.6c0 1.2-1.1 2-2.3 1.7l-16.1-4.3-.8 9.4 6 4.4c.7.5 1.1 1.3 1.1 2.1v1.9c0 1.1-1 2-2.2 1.8l-6.4-1.4-1.4 5.1c-.3 1.2-1.4 2-2.6 2h-.4c-1.2 0-2.3-.8-2.6-2l-1.4-5.1-6.4 1.4c-1.2.2-2.2-.7-2.2-1.8v-1.9c0-.8.4-1.6 1.1-2.1l6-4.4-.8-9.4-16.1 4.3c-1.2.3-2.3-.5-2.3-1.7v-2.6c0-1 .6-2 1.5-2.5l16.9-9.1.7-11c.2-3 1.8-4.9 4.1-4.9z" fill="#f2f7fa" stroke="#0a1620" stroke-width="2.5" stroke-linejoin="round"/>
      <path class="accent" d="M32 6.6c1.6 0 2.9 1.4 2.9 3.1v4.6h-5.8v-4.6c0-1.7 1.3-3.1 2.9-3.1z"/>
      <path d="M27.4 21.6h9.2" stroke="#0a1620" stroke-width="1.6" opacity=".55"/>
    </svg>`,
  arrow: `<svg viewBox="0 0 64 64" aria-hidden="true">
      <path class="accent-warm" d="M32 3.6 45.9 50.7c.5 1.6-1.3 2.9-2.7 1.8L32 45.2l-11.2 7.3c-1.4 1.1-3.2-.2-2.7-1.8L32 3.6z" stroke="#0a1620" stroke-width="2.6" stroke-linejoin="round"/>
      <circle cx="32" cy="31" r="3.2" fill="#0a1620" opacity=".5"/>
    </svg>`,
  classic: `<svg viewBox="0 0 64 64" aria-hidden="true">
      <path class="accent" d="M32 4 56.5 56.5 32 45.4 7.5 56.5z" stroke="#0a1620" stroke-width="2.6" stroke-linejoin="round"/>
      <circle cx="32" cy="36" r="3" fill="#0a1620" opacity=".45"/>
    </svg>`,
  // Used automatically when the bridge reports the Train Sim World source. Drawn nose-up like the
  // aircraft icons so the same rotation maths applies, but the head is at the top of the viewBox.
  train: `<svg viewBox="0 0 64 64" aria-hidden="true">
      <path d="M32 3.5c3.9 0 7 3.1 7 7v6.2c0 2.6-1.4 4.9-3.6 6.1 6.4 2.2 11.1 8.3 11.1 15.5v14.4c0 3.1-2.5 5.6-5.6 5.6H23.1c-3.1 0-5.6-2.5-5.6-5.6V38.3c0-7.2 4.7-13.3 11.1-15.5-2.2-1.2-3.6-3.5-3.6-6.1v-6.2c0-3.9 3.1-7 7-7Z" fill="#dfe8ee" stroke="#0a1620" stroke-width="2.6" stroke-linejoin="round"/>
      <path d="M25.4 12.5h13.2v6.4H25.4z" fill="#2f4f63" stroke="#0a1620" stroke-width="1.6"/>
      <path d="M20.8 33h22.4v9.4H20.8z" fill="#2f4f63" stroke="#0a1620" stroke-width="1.6"/>
      <path class="accent-stroke" d="M27.6 6.6h8.8" stroke-width="3" stroke-linecap="round"/>
      <circle class="accent-warm" cx="24.5" cy="52.5" r="2.6" stroke="#0a1620" stroke-width="1.4"/>
      <circle class="accent-warm" cx="39.5" cy="52.5" r="2.6" stroke="#0a1620" stroke-width="1.4"/>
    </svg>`
};
let aircraftIconName = '';

// The configured source, straight from /api/config. The protocol of the frames actually arriving
// takes precedence; this is the fallback used before the first frame.
let dataSource = { isTrain: false, protocol: '' };

function aircraftHtml() {
  // The train source has exactly one icon: it is not a style choice, it is what the map is
  // showing, so the aircraft icon picker is hidden in that mode.
  const name = dataSource.isTrain ? 'train' : (AIRCRAFT_ICONS[aircraftIconName] ? aircraftIconName : 'modern');
  return `<div class="aircraft-marker aircraft-${name}">${AIRCRAFT_ICONS[name]}</div>`;
}

const aircraft = L.marker([0, 0], {
  icon: L.divIcon({ className: 'aircraft-wrapper', html: aircraftHtml(), iconSize: [42, 42], iconAnchor: [21, 21] }),
  interactive: false, keyboard: false
});

function applyAircraftIcon(name, rebuild = true) {
  aircraftIconName = AIRCRAFT_ICONS[name] ? name : 'modern';
  store('efb.aircraftIcon', aircraftIconName);
  const select = $('#aircraftIcon');
  if (select && select.value !== aircraftIconName) select.value = aircraftIconName;
  if (!rebuild) return;
  aircraft.setIcon(L.divIcon({ className: 'aircraft-wrapper', html: aircraftHtml(), iconSize: [42, 42], iconAnchor: [21, 21] }));
  applyAircraftRotation();
}

// ------------------------------------------------------------------ source mode
// The bridge can be driven by X-Plane 12 or by Train Sim World 6. Everything that differs between
// the two is decided here, once, so the rest of the file can keep reading the same telemetry fields.
// The decisions themselves are pure functions in protocol.js, which is unit tested.
function applySourceMode(info) {
  const previous = dataSource.isTrain;
  dataSource = info;
  const train = info.isTrain;
  const visibility = panelVisibility(train);
  const labels = instrumentLabels(train);

  document.body.classList.toggle('mode-train', train);
  aircraft.setIcon(L.divIcon({ className: 'aircraft-wrapper', html: aircraftHtml(), iconSize: [42, 42], iconAnchor: [21, 21] }));

  // Instrument labels: the train reads km/h, shows the active speed limit where the aircraft shows
  // altitude, and the distance to the next limit where the aircraft shows vertical speed.
  $('#gsUnit').textContent = labels.speed;
  $('#altUnit').textContent = labels.altitude;
  $('#altLabel').textContent = train ? '限速' : 'ALT';
  $('#vsLabel').textContent = train ? '下一限速' : 'V/S';
  $('#vsUnit').textContent = train ? '' : labels.vertical;
  // The game API exposes no heading, so the instrument is removed rather than shown empty.
  $('#hdgInstrument').classList.toggle('hidden', !visibility.headingInstrument);

  // Aviation-only panels: SimBrief routes, the airport ground layer and airline badges.
  $('#simbriefSection').classList.toggle('hidden', !visibility.simbrief);
  $('#groundToggle').classList.toggle('hidden', !visibility.ground);
  $('#routeToggleRow').classList.toggle('hidden', !visibility.simbrief);
  // setGroundEnabled()/setRouteVisible() consult the current mode themselves and keep the user's
  // preference in dataset.wanted, so re-applying the stored values is what actually turns the
  // aviation-only layers off for a train and back on for an aircraft.
  setGroundEnabled($('#groundToggle').dataset.wanted === '1');
  setRouteVisible($('#routeToggle').checked);
  // The flight card and the train card swap places; the flight card's own visibility preference is
  // preserved by setFlightOverlay, which knows the current mode.
  setFlightOverlay($('#flightOverlay').dataset.wanted !== '0');
  $('#trainCard').classList.toggle('hidden', !train || $('#trainCard').dataset.wanted === '0');
  $('#trainToggle').classList.toggle('hidden', !train);
  $('#flightToggleButton').classList.toggle('hidden', train);

  // The aircraft icon picker has no meaning for a train, and the flight card is replaced by the
  // train status card below.
  $('#aircraftIconRow').classList.toggle('hidden', train);
  // In train mode the third diagnostics row shows the TSW endpoint state instead of the ground layer.
  $('#groundDiagnosticLabel').textContent = train ? 'TSW 端点' : '地面图层';
  if (!train) setText('#groundDiagnostic', groundDiagnosticText());

  if (previous !== train) {
    // Switching modes invalidates what belongs to the other simulator. The route line is cleared but
    // its toggle is left alone: the preference is re-applied by setRouteVisible above, which already
    // knows the mode, so returning to X-Plane restores exactly what the user had.
    if (train) routeWaypoints = [];
    else $('#groundToggle').title = groundButtonTitle();
    renderAirline();
  }
  renderAttribution();
}

// The track lives in its own canvas pane above the ground layer. A canvas path
// handles tens of thousands of vertices without the stalls an SVG polyline of
// the same length would cause on an iPad.
const trackPane = map.createPane('trackPane');
trackPane.style.zIndex = '360';
trackPane.style.pointerEvents = 'none';
const trackRenderer = L.canvas({ pane: 'trackPane', padding: 0.5 });

// Layered strokes: wide glow, dark casing, bright core. A canvas path cannot use
// a gradient along its length, so the three strokes provide depth and keep the
// track readable over bright OSM tiles.
const trackGlow = L.polyline([], { renderer: trackRenderer, pane: 'trackPane', color: mapPalette.track.glow, weight: 15, opacity: 0.1, lineCap: 'round', lineJoin: 'round', interactive: false, smoothFactor: 1 });
const trackCasing = L.polyline([], { renderer: trackRenderer, pane: 'trackPane', color: mapPalette.track.casing, weight: 9, opacity: 0.5, lineCap: 'round', lineJoin: 'round', interactive: false, smoothFactor: 1 });
const trackCore = L.polyline([], { renderer: trackRenderer, pane: 'trackPane', color: mapPalette.track.core, weight: 4, opacity: 0.95, lineCap: 'round', lineJoin: 'round', interactive: false, smoothFactor: 1 });
const trackStart = L.circleMarker([0, 0], { renderer: trackRenderer, pane: 'trackPane', radius: 6, color: mapPalette.trackStart.ring, weight: 2, fillColor: mapPalette.trackStart.fill, fillOpacity: 1, interactive: false });
const trackGroup = L.layerGroup([trackGlow, trackCasing, trackCore]).addTo(map);
let trackStartVisible = false;

// Retention policies live in track-retention.js (pure functions, unit tested).
let trackOption = 'forever';
let trackPoints = [];             // { lat, lng, t }

// ---------------------------------------------------------------- route overlay
const routeLineGlow = L.polyline([], { color: mapPalette.route.glow, weight: 11, opacity: 0.12, lineCap: 'round', dashArray: '1 10', interactive: false });
const routeLineCasing = L.polyline([], { color: mapPalette.route.casing, weight: 6, opacity: 0.55, lineCap: 'round', dashArray: '1 10', interactive: false });
const routeLine = L.polyline([], { color: mapPalette.route.core, weight: 3, opacity: 0.9, lineCap: 'round', dashArray: '1 10', interactive: false });
const routeGroup = L.layerGroup([routeLineGlow, routeLineCasing, routeLine]);
const routeDetail = L.layerGroup();
const routeLabels = L.layerGroup();
let routeShown = false;
let routeWaypoints = [];

// Waypoint symbols are rebuilt rather than restyled (circle markers keep the
// style they were created with), so the radii live here and the colours come
// from the active palette.
const WAYPOINT_RADIUS = { wpt: 3.5, vor: 4.5, ndb: 4.5 };
function waypointStyle(type) {
  const key = mapPalette.waypoint[type] ? type : 'wpt';
  return {
    radius: WAYPOINT_RADIUS[key],
    color: mapPalette.waypoint[key].ring,
    weight: 1.6,
    fillColor: mapPalette.waypoint[key].fill,
    fillOpacity: 0.95
  };
}

// Names stay useful on a wide view as well, so the floor is deliberately low (5
// covers a continent). Overlap is handled by the collision filter below instead
// of by hiding every name.
const WAYPOINT_LABEL_MIN_ZOOM = 5;
const WAYPOINT_LABEL_LIMIT = 90;
const labelPool = [];
let labelKey = "";


function waypointTooltip(point) {
  const parts = [`<strong>${escapeHtml(point.ident)}</strong>`];
  if (point.via) parts.push(`· ${escapeHtml(point.via)}`);
  if (Number.isFinite(point.altitudeFt)) parts.push(`· ${Math.round(point.altitudeFt).toLocaleString('en-US')} ft`);
  return `<span class="rot-inv">${parts.join(' ')}</span>`;
}

function airportLabel(point, kind) {
  const caption = kind === 'origin' ? '起' : kind === 'destination' ? '降' : '备';
  return L.marker([point.lat, point.lon], {
    interactive: false,
    keyboard: false,
    icon: L.divIcon({
      className: 'route-airport-label',
      iconSize: [0, 0],
      html: `<span class="rot-inv ${kind}"><b>${caption}</b>${escapeHtml(point.ident)}</span>`
    })
  });
}

function drawRoute(plan) {
  routeGroup.clearLayers();
  routeDetail.clearLayers();
  routeWaypoints = [];
  routeLabels.clearLayers();
  labelKey = "";
  if (!plan) return;

  const origin = plan.origin && Number.isFinite(plan.origin.lat) ? plan.origin : null;
  const destination = plan.destination && Number.isFinite(plan.destination.lat) ? plan.destination : null;
  const points = [];
  if (origin) points.push([origin.lat, origin.lon]);
  for (const waypoint of plan.waypoints || []) {
    if (Number.isFinite(waypoint.lat) && Number.isFinite(waypoint.lon)) points.push([waypoint.lat, waypoint.lon]);
  }
  if (destination) points.push([destination.lat, destination.lon]);

  if (points.length > 1) {
    routeLineGlow.setLatLngs(points);
    routeLineCasing.setLatLngs(points);
    routeLine.setLatLngs(points);
    routeGroup.addLayer(routeLineGlow);
    routeGroup.addLayer(routeLineCasing);
    routeGroup.addLayer(routeLine);
  }

  for (const waypoint of plan.waypoints || []) {
    if (!Number.isFinite(waypoint.lat) || !Number.isFinite(waypoint.lon)) continue;
    if (waypoint.type === 'apt' || waypoint.type === 'origin' || waypoint.type === 'destination') continue;
    const marker = L.circleMarker([waypoint.lat, waypoint.lon], { ...waypointStyle(waypoint.type), interactive: false });
    marker.bindTooltip(waypointTooltip(waypoint), { direction: 'top', offset: [0, -6], className: 'route-tooltip' });
    routeDetail.addLayer(marker);
    routeWaypoints.push(waypoint);
  }

  const airports = [];
  if (origin) airports.push({ ...origin, kind: 'origin' });
  if (destination) airports.push({ ...destination, kind: 'destination' });
  for (const alternate of plan.alternates || []) {
    if (alternate && Number.isFinite(alternate.lat)) airports.push({ ...alternate, kind: 'alternate' });
  }
  for (const airport of airports) {
    const color = mapPalette.airport[airport.kind] ?? mapPalette.airport.destination;
    routeDetail.addLayer(L.circleMarker([airport.lat, airport.lon], {
      radius: 6, color, weight: 2.5,
      fillColor: mapPalette.airport.fill, fillOpacity: 0.9, interactive: false
    }));
    routeDetail.addLayer(airportLabel(airport, airport.kind));
  }
  if (routeShown) {
    routeGroup.addTo(map);
    routeDetail.addTo(map);
  }
  scheduleWaypointLabels(true);
}

function setRouteVisible(visible) {
  // A train has no SimBrief plan, so the overlay is never drawn in train mode whatever the toggle
  // says. The toggle itself is left untouched as the user's preference for the aircraft mode.
  routeShown = visible && !dataSource.isTrain;
  if (routeShown) {
    routeGroup.addTo(map);
    routeDetail.addTo(map);
    routeLabels.addTo(map);
    scheduleWaypointLabels(true);
  } else {
    routeGroup.removeFrom(map);
    routeDetail.removeFrom(map);
    routeLabels.removeFrom(map);
  }
}

// ------------------------------------------------------------------ waypoint names
function ensureLabelPool(count) {
  while (labelPool.length < count) {
    labelPool.push(L.marker([0, 0], {
      interactive: false,
      keyboard: false,
      icon: L.divIcon({ className: 'route-wpt-label', iconSize: [0, 0], html: '<span class="rot-inv"></span>' })
    }));
  }
}

function updateWaypointLabels() {
  if (!routeShown || routeWaypoints.length === 0 || map.getZoom() < WAYPOINT_LABEL_MIN_ZOOM) {
    if (labelKey !== "off") {
      routeLabels.clearLayers();
      labelKey = "off";
    }
    return;
  }
  const key = `${map.getZoom()}|${map.getCenter().lat.toFixed(2)}|${map.getCenter().lng.toFixed(2)}|${routeWaypoints.length}`;
  if (key === labelKey) return;
  labelKey = key;

  const size = map.getSize();
  const bounds = map.getBounds().pad(0.12);
  const zoom = map.getZoom();
  const fontPx = zoom <= 6 ? 12 : 14;
  // Radio navaids first (they are what you read on a wide view), then the fixes
  // closest to the middle of the screen.
  const rank = (waypoint) => (waypoint.type === 'vor' || waypoint.type === 'ndb' ? 0 : 1);
  const candidates = [];
  for (const waypoint of routeWaypoints) {
    if (!bounds.contains([waypoint.lat, waypoint.lon])) continue;
    const point = map.latLngToContainerPoint([waypoint.lat, waypoint.lon]);
    candidates.push({
      waypoint,
      point,
      rank: rank(waypoint),
      distance: Math.hypot(point.x - size.x / 2, point.y - size.y / 2)
    });
  }
  candidates.sort((a, b) => a.rank - b.rank || a.distance - b.distance);

  const aircraftPoint = map.hasLayer(aircraft) ? map.latLngToContainerPoint(aircraft.getLatLng()) : null;
  ensureLabelPool(WAYPOINT_LABEL_LIMIT);
  const placed = [];
  let used = 0;
  for (const candidate of candidates) {
    if (used >= WAYPOINT_LABEL_LIMIT) break;
    const label = candidate.waypoint.ident ?? '';
    // Rectangle sized from the real text, so short idents do not waste space and
    // long ones cannot collide with their neighbour.
    const width = 14 + label.length * fontPx * 0.62;
    const height = fontPx + 12;
    const box = {
      x1: candidate.point.x - width / 2,
      x2: candidate.point.x + width / 2,
      y1: candidate.point.y - height / 2,
      y2: candidate.point.y + height / 2
    };
    // Never put a name on top of the aircraft icon.
    if (aircraftPoint
      && Math.abs(aircraftPoint.x - candidate.point.x) < width / 2 + 24
      && Math.abs(aircraftPoint.y - candidate.point.y) < height / 2 + 22) continue;
    if (placed.some((other) => box.x1 < other.x2 + 3 && other.x1 < box.x2 + 3 && box.y1 < other.y2 + 3 && other.y1 < box.y2 + 3)) continue;
    placed.push(box);
    const marker = labelPool[used++];
    marker.setLatLng([candidate.waypoint.lat, candidate.waypoint.lon]);
    routeLabels.addLayer(marker);
    const element = marker.getElement()?.firstElementChild;
    if (element) {
      element.textContent = label;
      element.style.fontSize = `${fontPx}px`;
    }
  }
  for (let index = used; index < labelPool.length; index++) routeLabels.removeLayer(labelPool[index]);
}

let labelTimer;
function scheduleWaypointLabels(immediate = false) {
  clearTimeout(labelTimer);
  if (immediate) { updateWaypointLabels(); return; }
  labelTimer = setTimeout(updateWaypointLabels, 140);
}

// ---------------------------------------------------------------- live state
let follow = true;
let headingUp = false;
let firstPosition = true;
let lastTelemetry = null;
let socket;
let reconnectTimer;

function format(value, digits = 0) {
  return Number.isFinite(value) ? Math.round(value).toLocaleString('en-US', { maximumFractionDigits: digits }) : '—';
}

function setStatus(kind, text) {
  $('#status').className = `status ${kind}`;
  $('#statusText').textContent = text;
}

// The bridge records the flown track and this page only reads it back.
//
// That split is deliberate: while iPadOS has Safari suspended (screen locked,
// another app in front, the socket dropped) this page is not running at all, so
// anything it "records" has a hole in it. The PC is always running, so it keeps
// the track continuous and hands it over on request - the page just draws it.
function renderTrack() {
  const latlngs = trackPoints.map((point) => [point.lat, point.lng]);
  trackCore.setLatLngs(latlngs);
  trackCasing.setLatLngs(latlngs);
  // The wide glow is the most expensive stroke, so it is dropped on very long
  // tracks where the casing already provides the contrast.
  const wantGlow = latlngs.length <= 12000;
  if (wantGlow && !trackGroup.hasLayer(trackGlow)) trackGroup.addLayer(trackGlow);
  if (!wantGlow && trackGroup.hasLayer(trackGlow)) trackGroup.removeLayer(trackGlow);
  if (wantGlow) trackGlow.setLatLngs(latlngs);
  if (latlngs.length > 0) {
    trackStart.setLatLng(latlngs[0]);
    if (!trackStartVisible) {
      trackGroup.addLayer(trackStart);
      trackStartVisible = true;
    }
  } else if (trackStartVisible) {
    trackGroup.removeLayer(trackStart);
    trackStartVisible = false;
  }
  updateTrackSummary();
}

function formatSpan(milliseconds) {
  const total = Math.max(0, Math.round(milliseconds / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  if (hours > 0) return `${hours} 小时 ${String(minutes).padStart(2, '0')} 分`;
  if (minutes > 0) return `${minutes} 分 ${String(seconds).padStart(2, '0')} 秒`;
  return `${seconds} 秒`;
}

function updateTrackSummary() {
  const node = $('#trackSummary');
  if (!node) return;
  if (trackPoints.length === 0) {
    node.textContent = '轨迹为空，清轨迹按钮可随时清空。';
    return;
  }
  const span = trackPoints[trackPoints.length - 1].t - trackPoints[0].t;
  const hidden = map.hasLayer(trackGroup) ? '' : '（轨迹已隐藏）';
  const stale = trackStatus === 'ready' ? '' : '（电脑端未响应）';
  node.textContent = `电脑端已记录 ${trackPoints.length.toLocaleString('en-US')} 个点 · 跨度 ${formatSpan(span)}${hidden}${stale}`;
  if (typeof pushSettingsInfo === 'function') pushSettingsInfo();
}

let trackStatus = 'ready';
let trackPollTimer = 0;
let trackRequest = null;

function trackUrl(since) {
  const query = new URLSearchParams();
  if (Number.isFinite(since)) query.set('since', String(Math.round(since)));
  const suffix = query.toString();
  return `/api/track${suffix ? `?${suffix}` : ''}`;
}

// since === null asks for the whole retained track; a timestamp asks for what
// happened since, which is how a page that was asleep for ten minutes catches up
// in one request.
async function loadTrack(since = null) {
  try {
    const response = await fetch(trackUrl(since));
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const data = await response.json();
    const incoming = (data.points || [])
      .filter((point) => Array.isArray(point) && point.length >= 3)
      .map((point) => ({ lat: point[0], lng: point[1], t: point[2] }));
    if (data.replaced || since === null || trackPoints.length === 0) {
      trackPoints = incoming;
    } else {
      const newest = trackPoints[trackPoints.length - 1].t;
      trackPoints = trackPoints.concat(incoming.filter((point) => point.t > newest));
    }
    // The bridge keeps everything it is allowed to record; how much of it is
    // drawn is still this page's decision (the "航迹保留" selector).
    trackPoints = pruneTrackPoints(trackPoints, trackOption);
    trackStatus = 'ready';
    renderTrack();
  } catch {
    // An older bridge has no /api/track; say so instead of drawing nothing.
    trackStatus = 'error';
    updateTrackSummary();
  }
}

function pollTrack() {
  if (document.hidden) return;               // no point asking while suspended
  if (trackRequest) return;                  // one request in flight at a time
  const newest = trackPoints.length > 0 ? trackPoints[trackPoints.length - 1].t : null;
  trackRequest = loadTrack(newest).finally(() => { trackRequest = null; });
}

async function clearTrack() {
  try {
    await fetch('/api/track', { method: 'DELETE' });
  } catch { /* the local copy is cleared either way */ }
  trackPoints = [];
  renderTrack();
  showToast('电脑端的轨迹已清除');
}

function setTrackOption(value) {
  trackOption = TRACK_OPTIONS[value] ? value : 'forever';
  store('efb.trackOption', trackOption);
  const select = $('#trackOption');
  if (select && select.value !== trackOption) select.value = trackOption;
  // Nothing to ask the bridge: the recorded track is unchanged, only how much of
  // it is drawn.
  trackPoints = pruneTrackPoints(trackPoints, trackOption);
  renderTrack();
}

function initTrack() {
  const stored = readStored('efb.trackOption');
  trackOption = TRACK_OPTIONS[stored] ? stored : 'forever';
  const select = $('#trackOption');
  if (select) select.value = trackOption;
  loadTrack(null);
  trackPollTimer = setInterval(pollTrack, 2000);
  // Coming back from the background is exactly the case that used to lose the
  // middle of the flight: ask for the missed section straight away.
  document.addEventListener('visibilitychange', () => { if (!document.hidden) pollTrack(); });
  window.addEventListener('online', () => pollTrack());
}

// ------------------------------------------------------------- train status
// Train Sim World 6 has no flight plan, so the flight card cannot be reused: the driver-relevant
// numbers come from the game's driver-aid endpoint and are absent whenever that endpoint is not
// available. Everything the front end knows about them is in protocol.js.
const TRAIN_FIELDS = ['tcSpeed', 'tcLimit', 'tcNextLimit', 'tcLimitDistance', 'tcSignal', 'tcSignalDistance', 'tcGradient'];

function setTrainCard(visible) {
  $('#trainCard').dataset.wanted = visible ? '1' : '0';
  // Like the flight card, this one only exists in train mode; the preference is kept so switching
  // back to the train source restores what the user chose.
  $('#trainCard').classList.toggle('hidden', !visible || !dataSource.isTrain);
  $('#trainToggle').classList.toggle('active', visible && dataSource.isTrain);
  $('#trainToggle').classList.toggle('hidden', !dataSource.isTrain);
  store('efb.trainVisible', visible ? '1' : '0');
}

function renderTrainStatus(data) {
  if (!data || !dataSource.isTrain) return;
  const speed = primarySpeed(data, true);
  setText('#tcSpeed', speed === null ? '—' : `${format(speed, 1)} km/h`);
  setText('#tcLimit', Number.isFinite(data.limitKmh) ? `${Math.round(data.limitKmh)} km/h` : '—');
  setText('#tcNextLimit', Number.isFinite(data.nextLimitKmh) ? `${Math.round(data.nextLimitKmh)} km/h` : '—');
  setText('#tcLimitDistance', formatMetres(data.distanceToNextLimitM));
  setText('#tcSignalDistance', formatMetres(data.distanceToSignalM));
  setText('#tcSignal', data.signalAspect || '—');
  setText('#tcGradient', Number.isFinite(data.gradient) ? `${data.gradient > 0 ? '+' : ''}${data.gradient}‰` : '—');
  setText('#tcService', data.currentServiceName || '列车');
  setText('#tcPhase', trainPhase(speed));
  setText('#tcUpdated', data.receivedAt ? `${new Date(data.receivedAt).toISOString().slice(11, 19)}Z` : '—');
}

function updateTelemetry(data) {
  lastTelemetry = data;
  // The frame in hand decides which simulator is on screen: it is more trustworthy than the
  // configuration, which may already describe the source the bridge is switching to.
  const info = sourceInfo(data.protocol, dataSource.configured);
  if (info.isTrain !== dataSource.isTrain) {
    dataSource = { ...info, configured: dataSource.configured };
    applySourceMode(dataSource);
  }

  const speed = primarySpeed(data, dataSource.isTrain);
  $('#gs').textContent = speed === null ? '—' : format(speed, dataSource.isTrain ? 1 : 0);
  const secondary = secondaryReadouts(data, dataSource.isTrain);
  $('#alt').textContent = dataSource.isTrain
    ? (Number.isFinite(secondary.altitude) ? String(Math.round(secondary.altitude)) : '—')
    : format(secondary.altitude);
  $('#hdg').textContent = Number.isFinite(data.headingTrueDeg) ? String(Math.round(wrap360(data.headingTrueDeg))).padStart(3, '0') : '—';
  $('#vs').textContent = dataSource.isTrain ? formatMetres(secondary.vertical) : format(secondary.vertical);
  $('#sourceDiagnostic').textContent = `${data.source || '—'} · ${data.protocol || '—'}`;
  setStatus('live', statusText('live', dataSource.isTrain) + (data.protocol === 'DEMO' ? '（演示）' : ''));

  if (!Number.isFinite(data.latitude) || !Number.isFinite(data.longitude)) return;
  const position = [data.latitude, data.longitude];
  aircraft.setLatLng(position);
  if (!map.hasLayer(aircraft) && $('#aircraftToggle').checked) aircraft.addTo(map);
  applyAircraftRotation();
  updateHeading(Number.isFinite(data.headingTrueDeg) ? data.headingTrueDeg : null);

  // The track is not recorded here: the bridge owns it (see loadTrack below).
  if (firstPosition) {
    map.setView(position, 12, { animate: false });
    firstPosition = false;
  } else if (follow) {
    map.panTo(position, { animate: true, duration: 0.25, noMoveStart: true });
  }
  renderFlightStatus();
}

function connect() {
  clearTimeout(reconnectTimer);
  const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  socket = new WebSocket(`${protocol}//${location.host}/ws`);
  $('#wsDiagnostic').textContent = '连接中';
  socket.addEventListener('open', () => {
    $('#wsDiagnostic').textContent = '已连接';
    if (!lastTelemetry) setStatus('waiting', statusText('waiting', dataSource.isTrain));
    // A reconnect may have followed a long gap: fill in the track the bridge
    // recorded while this page was off the air.
    pollTrack();
  });
  socket.addEventListener('message', (event) => {
    try {
      const message = JSON.parse(event.data);
      if (message.type === 'telemetry') updateTelemetry(message.payload);
      // The hello frame carries the configured source, so the right icon, units and panels are in
      // place before the first telemetry frame (which can take seconds in TSW mode).
      else if (message.type === 'hello' && message.payload?.source) {
        dataSource = { ...sourceInfo('', message.payload.source), configured: message.payload.source };
        applySourceMode(dataSource);
        if (!lastTelemetry) setStatus('waiting', statusText('waiting', dataSource.isTrain));
      }
    } catch (error) {
      console.warn('telemetry failed', error);
      showToast('收到无法解析的数据');
    }
  });
  socket.addEventListener('close', () => {
    $('#wsDiagnostic').textContent = '已断开，重连中';
    setStatus('lost', '桥接断开');
    reconnectTimer = setTimeout(connect, 1500);
  });
  socket.addEventListener('error', () => socket.close());
}

// ---------------------------------------------------------------- controls
function setFollow(enabled) {
  follow = enabled;
  $('#followButton').classList.toggle('active', follow);
  $('#followButton').setAttribute('aria-pressed', String(follow));
  if (follow && aircraft.getLatLng()?.lat) map.panTo(aircraft.getLatLng(), { animate: true, duration: 0.25 });
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
  rotator.classList.toggle('rotating', headingUp);
  layoutRotator();
  if (headingUp) {
    setFollow(true);
    map.dragging.disable();
    rotation.target = rotation.heading === null ? 0 : -rotation.heading;
    rotation.current = rotation.target;
    applyRotation();
    showToast('已切换为航向向上');
  } else {
    map.dragging.enable();
    rotation.target = 0;
    scheduleRotation();
    showToast('已切换为北向上');
  }
  // pan:true keeps the same latlng under the new container centre, so the
  // aircraft stays put when the container grows or shrinks.
  requestAnimationFrame(() => map.invalidateSize());
}

// One finger pans the map while heading-up is active. Leaflet's drag handler
// moves the map pane along screen axes, which is wrong once the container is
// rotated, so the pointer delta is rotated into container space first.
const pointers = new Map();
let panMoved = false;

function onPointerDown(event) {
  if (!headingUp || event.isPrimary === false) return;
  pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
  if (pointers.size === 1) panMoved = false;
}

function onPointerMove(event) {
  if (!headingUp || pointers.size !== 1) return;
  const previous = pointers.get(event.pointerId);
  if (!previous) return;
  const dx = event.clientX - previous.x;
  const dy = event.clientY - previous.y;
  previous.x = event.clientX;
  previous.y = event.clientY;
  if (!dx && !dy) return;
  if (!panMoved && Math.abs(dx) + Math.abs(dy) < 3) return;
  if (!panMoved) { panMoved = true; setFollow(false); }
  const angle = -rotation.current * RAD;
  const cos = Math.cos(angle);
  const sin = Math.sin(angle);
  map.panBy([-(dx * cos - dy * sin), -(dx * sin + dy * cos)], { animate: false });
}

function onPointerEnd(event) {
  pointers.delete(event.pointerId);
  if (pointers.size === 0) panMoved = false;
}

map.getContainer().addEventListener('pointerdown', onPointerDown);
window.addEventListener('pointermove', onPointerMove);
window.addEventListener('pointerup', onPointerEnd);
window.addEventListener('pointercancel', onPointerEnd);

// iPad Safari ignores user-scalable=no, so the page-level pinch gesture is
// suppressed here; the map keeps its own two-finger pinch zoom.
['gesturestart', 'gesturechange', 'gestureend'].forEach((type) =>
  document.addEventListener(type, (event) => event.preventDefault(), { passive: false }));

function switchBaseLayer(name) {
  const next = baseLayers.get(name);
  if (!next || next === activeBaseLayer) return;
  if (map.hasLayer(activeBaseLayer)) map.removeLayer(activeBaseLayer);
  next.addTo(map);
  activeBaseLayer = next;
  markBaseLayer();
  renderAttribution();
}

// Marks whichever tile layer is currently the base map, so night mode can filter
// exactly that layer (and never the weather overlay).
function markBaseLayer() {
  for (const layer of baseLayers.values()) {
    const container = layer.getContainer?.();
    if (container) container.classList.toggle('efb-base-tiles', layer === activeBaseLayer);
  }
}

function applyNightMode(enabled) {
  document.body.classList.toggle('night', Boolean(enabled));
  store('efb.nightMode', enabled ? '1' : '0');
  const toggle = $('#nightToggle');
  if (toggle) toggle.checked = Boolean(enabled);
  syncMirror('#nightToggleMirror', Boolean(enabled));
  // Night mode now switches the whole interface, not just the tiles: the pages
  // and the map overlays follow the same palette.
  mapPalette = enabled ? MAP_PALETTE.night : MAP_PALETTE.day;
  applyMapPalette();
}

function applyMapPalette() {
  trackGlow.setStyle({ color: mapPalette.track.glow });
  trackCasing.setStyle({ color: mapPalette.track.casing });
  trackCore.setStyle({ color: mapPalette.track.core });
  trackStart.setStyle({ color: mapPalette.trackStart.ring, fillColor: mapPalette.trackStart.fill });
  routeLineGlow.setStyle({ color: mapPalette.route.glow });
  routeLineCasing.setStyle({ color: mapPalette.route.casing });
  routeLine.setStyle({ color: mapPalette.route.core });
  // Markers keep the style they were created with, so the themed ones are rebuilt.
  if (flightPlan) drawRoute(flightPlan);
  if (groundEnabled) updateGroundLayer(true);
}

function renderAttribution() {
  const parts = [activeBaseLayer?.getAttribution?.() || ''];
  if (weatherLayer) parts.push(weatherLayer.getAttribution());
  $('#mapAttribution').innerHTML = parts.filter(Boolean).join(' · ');
}

// The rail and the page switching live in pages.js; this file only has to know
// that the layout changed (Leaflet needs to re-measure) and which page is up.
let toastTimer;
function showToast(message) {
  $('#toast').textContent = message;
  $('#toast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('#toast').classList.remove('show'), 2200);
}

$('#followButton').addEventListener('click', () => setFollow(!follow));
$('#headingUpButton').addEventListener('click', () => setHeadingUp(!headingUp));
$('#zoomInButton').addEventListener('click', () => map.zoomIn());
$('#zoomOutButton').addEventListener('click', () => map.zoomOut());
$('#clearTrackButton').addEventListener('click', () => clearTrack());
$('#trackToggle').addEventListener('change', (event) => {
  if (event.target.checked) trackGroup.addTo(map);
  else trackGroup.removeFrom(map);
  updateTrackSummary();
  syncMirror('#trackToggleMirror', event.target.checked);
});
$('#trackOption').addEventListener('change', (event) => setTrackOption(event.target.value));
$('#aircraftIcon').addEventListener('change', (event) => applyAircraftIcon(event.target.value));
$('#aircraftToggle').addEventListener('change', (event) => event.target.checked ? aircraft.addTo(map) : aircraft.removeFrom(map));
$('#routeToggle').addEventListener('change', (event) => setRouteVisible(event.target.checked));
$('#simbriefRefreshButton').addEventListener('click', () => loadFlightPlan(true));
$('#planSelect').addEventListener('change', (event) => selectPlan(event.target.value));
$('#ofpButton').addEventListener('click', () => openOfp());

// The 设置 page repeats the two most used switches. They are the same setting,
// so both directions stay in step instead of drifting apart.
function syncMirror(selector, checked) {
  const mirror = $(selector);
  if (mirror && mirror.checked !== checked) mirror.checked = checked;
}
$('#trackToggleMirror').addEventListener('change', (event) => {
  const real = $('#trackToggle');
  real.checked = event.target.checked;
  real.dispatchEvent(new Event('change'));
});
$('#nightToggleMirror').addEventListener('change', (event) => applyNightMode(event.target.checked));
$('#ofpCloseButton').addEventListener('click', closeOfp);
$('#ofpView').addEventListener('click', (event) => { if (event.target === $('#ofpView')) closeOfp(); });
$('#weatherToggle').addEventListener('click', () => {
  const sub = $('#weatherSubIcons');
  const open = sub.classList.contains('hidden');
  sub.classList.toggle('hidden', !open);
  $('#weatherOpacityRow').classList.toggle('hidden', !open);
  $('#weatherToggle').setAttribute('aria-expanded', String(open));
});
$('#weatherOpacity').addEventListener('input', (event) => applyWeatherOpacity(Number(event.target.value)));
$('#groundToggle').addEventListener('click', () => setGroundEnabled(!groundEnabled));
$('#foHideButton').addEventListener('click', () => setFlightOverlay(false));
$('#flightRestoreButton').addEventListener('click', () => setFlightOverlay(true));
$('#flightToggleButton').addEventListener('click', () => setFlightOverlay($('#flightOverlay').classList.contains('hidden')));
$('#trainToggle').addEventListener('click', () => setTrainCard($('#trainCard').classList.contains('hidden')));
$('#tcHideButton').addEventListener('click', () => setTrainCard(false));
$('#nightToggle').addEventListener('change', (event) => applyNightMode(event.target.checked));
$('#foCollapseButton').addEventListener('click', () => {
  const overlay = $('#flightOverlay');
  overlay.classList.toggle('collapsed');
  $('#foCollapseButton').textContent = overlay.classList.contains('collapsed') ? '▸' : '▾';
  store('efb.flightCollapsed', overlay.classList.contains('collapsed') ? '1' : '0');
});
document.addEventListener('keydown', (event) => { if (event.key === 'Escape') closeOfp(); });
map.on('dragstart', () => setFollow(false));
map.on('moveend', () => scheduleWaypointLabels());
map.on('zoomend', () => scheduleWaypointLabels());
map.on('zoomend', () => updateGroundLayer());
map.on('moveend', () => updateGroundLayer());

function resize() {
  layoutRotator();
  map.invalidateSize();
}
window.addEventListener('resize', resize);
window.addEventListener('orientationchange', () => setTimeout(resize, 250));
if ('ResizeObserver' in window) new ResizeObserver(() => resize()).observe(stage);
// The rail turns into an overlay below 900px (pages.js handles the scrim); the
// map still has to re-measure when that switch happens.
window.matchMedia('(max-width: 900px)').addEventListener('change', () => setTimeout(resize, 220));

document.querySelectorAll('input[name="base"]').forEach((input) =>
  input.addEventListener('change', (event) => switchBaseLayer(event.target.value)));

// ---------------------------------------------------------------- config + route data
async function loadConfig() {
  const response = await fetch('/api/config');
  if (!response.ok) throw new Error(`Config HTTP ${response.status}`);
  return response.json();
}

function setText(selector, value) {
  const element = $(selector);
  if (element) element.textContent = value;
}

// SimBrief sends "2026-09-10T22:55:00Z" and "09:47:00" style values.
function utcClock(iso) {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(String(iso ?? ''));
  return match ? `${match[4]}:${match[5]}` : '—';
}

function utcDate(iso) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(iso ?? ''));
  return match ? `${match[1]}${match[2]}${match[3]}` : '';
}

function formatDuration(seconds) {
  if (!Number.isFinite(seconds) || seconds <= 0) return '—';
  const minutes = Math.round(seconds / 60);
  return `${Math.floor(minutes / 60)}h${String(minutes % 60).padStart(2, '0')}m`;
}

// ------------------------------------------------------------- flight status
const MIN_AIRBORNE_GROUND_SPEED = 30; // kt; below this the aircraft is on the ground

let flightPlan = null;
let flightRoute = [];
let flightRouteTotal = 0;
let lastFlightRender = 0;
let lastFlightTelemetryAt = 0;
let airlines = null;
let airlineLogoUrlTemplate = '';

// Airline lookup by the ICAO three letter prefix of the callsign. The table is a
// static file (offline, no third party request); logos are only used when the
// operator configured a licensed source of their own.
async function loadAirlines() {
  try {
    const response = await fetch('/data/airlines.json');
    if (response.ok) airlines = await response.json();
  } catch { airlines = null; }
}

function airlineFor(callsign) {
  const match = /^([A-Z]{3})/.exec(String(callsign ?? '').toUpperCase());
  if (!match || !airlines) return null;
  const entry = airlines[match[1]];
  return entry ? { icao: match[1], name: entry.name || '', iata: entry.iata || '' } : { icao: match[1], name: '', iata: '' };
}

function airlineBadgeColor(icao) {
  let hash = 0;
  for (const character of icao) hash = (hash * 31 + character.charCodeAt(0)) % 360;
  return `hsl(${hash}, 62%, 62%)`;
}

function renderAirline() {
  const badge = $('#foAirlineBadge');
  const image = $('#foAirlineLogo');
  const box = $('#foAirline');
  if (!badge || !image || !box) return;
  const airline = airlineFor(flightPlan?.flight?.callsign);
  if (!airline) {
    box.classList.add('hidden');
    image.classList.add('hidden');
    return;
  }
  box.classList.remove('hidden');
  badge.textContent = airline.icao;
  badge.style.background = airlineBadgeColor(airline.icao);
  badge.title = airline.name || airline.icao;
  if (airlineLogoUrlTemplate.length > 0) {
    image.onerror = () => { image.classList.add('hidden'); badge.classList.remove('hidden'); };
    image.onload = () => { image.classList.remove('hidden'); badge.classList.add('hidden'); };
    image.src = airlineLogoUrlTemplate.replace('{icao}', airline.icao).replace('{iata}', airline.iata);
  } else {
    image.classList.add('hidden');
    badge.classList.remove('hidden');
  }
}

// Projects the aircraft onto the planned route and reports how much of it is
// still ahead. Returns null when there is no usable route.
function remainingAlongRoute(position) {
  if (flightRoute.length < 2) return null;
  let best = null;
  for (let index = 0; index < flightRoute.length - 1; index++) {
    const a = flightRoute[index];
    const b = flightRoute[index + 1];
    const k = Math.cos(a[0] * RAD);
    const vx = (b[1] - a[1]) * k;
    const vy = b[0] - a[0];
    const px = (position[1] - a[1]) * k;
    const py = position[0] - a[0];
    const length2 = vx * vx + vy * vy;
    const t = length2 > 0 ? Math.max(0, Math.min(1, (px * vx + py * vy) / length2)) : 0;
    const projected = [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
    const cross = nmBetween(position, projected);
    if (best !== null && cross >= best.cross) continue;
    let remaining = nmBetween(projected, b);
    for (let rest = index + 1; rest < flightRoute.length - 1; rest++) remaining += nmBetween(flightRoute[rest], flightRoute[rest + 1]);
    best = { cross, remaining };
  }
  return best ? best.remaining : null;
}

function flightPhase(groundSpeed, verticalSpeed) {
  if (!Number.isFinite(groundSpeed) || groundSpeed < MIN_AIRBORNE_GROUND_SPEED) return '地面';
  if (Number.isFinite(verticalSpeed) && verticalSpeed > 200) return '爬升';
  if (Number.isFinite(verticalSpeed) && verticalSpeed < -200) return '下降';
  return '巡航';
}

function setFlightOverlayPlan(plan) {
  flightPlan = plan ?? null;
  flightRoute = [];
  flightRouteTotal = 0;
  if (plan) {
    const points = [];
    if (Number.isFinite(plan.origin?.lat) && Number.isFinite(plan.origin?.lon)) points.push([plan.origin.lat, plan.origin.lon]);
    for (const waypoint of plan.waypoints || []) {
      if (Number.isFinite(waypoint.lat) && Number.isFinite(waypoint.lon)) points.push([waypoint.lat, waypoint.lon]);
    }
    if (Number.isFinite(plan.destination?.lat) && Number.isFinite(plan.destination?.lon)) points.push([plan.destination.lat, plan.destination.lon]);
    flightRoute = points;
    for (let index = 0; index < points.length - 1; index++) flightRouteTotal += nmBetween(points[index], points[index + 1]);
  }
  renderFlightPlanFields();
  renderFlightStatus(true);
}

function renderFlightPlanFields() {
  const flight = flightPlan?.flight;
  const type = [flight?.aircraftName, flight?.aircraft].filter(Boolean).join(' · ') || '—';
  const overnight = flight && utcDate(flight.plannedOn) !== utcDate(flight.plannedOff) ? '+1' : '';
  setText('#foCallsign', flight?.callsign || '—');
  setText('#foAircraft', flight ? [type, flight.registration].filter(Boolean).join(' · ') : '—');
  // The header carries the flight identity the way an airline EFB does, and
  // falls back to the product name when there is no plan.
  const route = flightPlan?.origin?.ident && flightPlan?.destination?.ident
    ? `${flightPlan.origin.ident} → ${flightPlan.destination.ident}`
    : '';
  setText('#brandTitle', flight?.callsign || 'PORTABLE EFB');
  setText('#brandSub', [route, type].filter(Boolean).join(' · ') || '局域网移动地图 EFB');
  const airlineName = airlineFor(flight?.callsign)?.name ?? '';
  if (airlineName) setText('#foAircraft', [airlineName, type, flight?.registration].filter(Boolean).join(' · '));
  renderAirline();
  setText('#foOrigin', flightPlan?.origin?.ident || '—');
  setText('#foDestination', flightPlan?.destination?.ident || '—');
  setText('#foPlannedOff', flight?.plannedOff ? utcClock(flight.plannedOff) : '—');
  setText('#foPlannedOn', flight?.plannedOn ? `${utcClock(flight.plannedOn)}${overnight ? ` ${overnight}` : ''}` : '—');
  setText('#foEnroute', formatDuration(flight?.plannedEnrouteSeconds ?? flightPlan?.eteSeconds));
}

function renderFlightStatus(force = false) {
  const data = lastTelemetry;
  const now = Date.now();
  if (!data) return;
  // Render every new telemetry frame (the very first one always), but never
  // more often than 4 Hz, so the card cannot show stale values when a single
  // frame is all that arrives.
  const isNewFrame = data.receivedAt !== lastFlightTelemetryAt;
  if (!force && !isNewFrame) return;
  if (!force && lastFlightTelemetryAt !== 0 && now - lastFlightRender < 250) return;
  lastFlightRender = now;
  lastFlightTelemetryAt = data.receivedAt;
  const position = Number.isFinite(data.latitude) && Number.isFinite(data.longitude) ? [data.latitude, data.longitude] : null;
  // The train card is fed from the same frame and is throttled by the same clock, so both cards
  // share this render point instead of each running its own timer.
  renderTrainStatus(data);
  const destination = flightPlan?.destination;
  const alongRoute = position ? remainingAlongRoute(position) : null;
  const direct = position && Number.isFinite(destination?.lat) && Number.isFinite(destination?.lon)
    ? nmBetween(position, [destination.lat, destination.lon])
    : null;
  const remaining = Number.isFinite(alongRoute) ? alongRoute : direct;
  const groundSpeed = Number.isFinite(data.groundSpeedKt) ? data.groundSpeedKt : null;
  const airborne = groundSpeed !== null && groundSpeed >= MIN_AIRBORNE_GROUND_SPEED;

  setText('#foRemaining', Number.isFinite(remaining)
    ? `${Math.round(remaining).toLocaleString('en-US')} NM${Number.isFinite(alongRoute) ? '' : '（直飞）'}`
    : '—');

  const etaDelta = $('#foEtaDelta');
  if (Number.isFinite(remaining) && airborne) {
    const seconds = (remaining / groundSpeed) * 3600;
    setText('#foRemainingTime', formatDuration(seconds));
    const eta = new Date(now + seconds * 1000);
    setText('#foEta', `${String(eta.getUTCHours()).padStart(2, '0')}:${String(eta.getUTCMinutes()).padStart(2, '0')}Z`);
    // On-time performance sits next to the ETA instead of inside it: the old
    // single string wrapped onto three lines on an iPad.
    let label = '';
    let late = false;
    const planned = flightPlan?.flight?.plannedOn;
    if (planned) {
      const difference = (eta.getTime() - Date.parse(planned)) / 1000;
      if (Number.isFinite(difference)) {
        if (Math.abs(difference) < 60) label = '准点';
        else {
          label = `${difference > 0 ? '晚' : '早'} ${formatDuration(Math.abs(difference))}`;
          late = difference > 0;
        }
      }
    }
    if (etaDelta) {
      etaDelta.textContent = label;
      etaDelta.classList.toggle('hidden', label.length === 0);
      etaDelta.classList.toggle('late', late);
      etaDelta.classList.toggle('early', label.startsWith('早'));
    }
  } else {
    setText('#foRemainingTime', '未知');
    setText('#foEta', Number.isFinite(remaining) ? '未知（地速不足）' : '未知');
    if (etaDelta) etaDelta.classList.add('hidden');
  }

  const total = flightRouteTotal > 0 ? flightRouteTotal : (Number.isFinite(flightPlan?.distanceNm) ? flightPlan.distanceNm : null);
  if (Number.isFinite(remaining) && Number.isFinite(total) && total > 0) {
    const progress = Math.max(0, Math.min(100, Math.round((1 - remaining / total) * 100)));
    setText('#foProgress', `${progress}%`);
    $('#foBarFill').style.width = `${progress}%`;
  } else {
    setText('#foProgress', '—');
    $('#foBarFill').style.width = '0%';
  }

  setText('#foGs', groundSpeed === null ? '—' : `${Math.round(groundSpeed)} kt`);
  setText('#foAlt', Number.isFinite(data.altitudeMslFt) ? `${Math.round(data.altitudeMslFt).toLocaleString('en-US')} ft` : '—');
  setText('#foHdg', Number.isFinite(data.headingTrueDeg) ? `${String(Math.round(wrap360(data.headingTrueDeg))).padStart(3, '0')}°` : '—');
  setText('#foPhase', flightPhase(groundSpeed, data.verticalSpeedFpm));
  setText('#foUpdated', data.receivedAt ? `${new Date(data.receivedAt).toISOString().slice(11, 19)}Z` : '—');
}

function setFlightOverlay(visible) {
  // Remembered separately from the rendered state: in train mode the card is forced hidden, and the
  // user's preference has to survive that so switching back restores what they chose.
  $('#flightOverlay').dataset.wanted = visible ? '1' : '0';
  const shown = visible && !dataSource.isTrain;
  $('#flightOverlay').classList.toggle('hidden', !shown);
  $('#flightToggleButton').classList.toggle('hidden', dataSource.isTrain);
  $('#flightToggleButton').classList.toggle('active', shown);
  $('#flightToggleButton').setAttribute('aria-pressed', String(shown));
  // Small restore icon in the left column, above the weather switcher.
  $('#flightRestoreButton').classList.toggle('hidden', shown || dataSource.isTrain);
  store('efb.flightVisible', visible ? '1' : '0');
}

function wireFlightDrag() {
  const overlay = $('#flightOverlay');
  const handle = $('#flightDragHandle');
  let startX = 0;
  let startY = 0;
  let originLeft = 0;
  let originTop = 0;
  const move = (event) => {
    const stageRect = stage.getBoundingClientRect();
    // Keep the card out of the top bar and out of the button columns so it can
    // never end up hidden behind them again.
    const topBar = document.querySelector('.topbar').getBoundingClientRect();
    const actions = document.querySelector('.map-actions').getBoundingClientRect();
    const minTop = Math.max(6, topBar.bottom - stageRect.top + 8);
    const maxLeft = stageRect.width - overlay.offsetWidth - 6;
    let left = Math.max(6, Math.min(maxLeft, originLeft + (event.clientX - startX)));
    let top = Math.max(minTop, Math.min(stageRect.height - overlay.offsetHeight - 6, originTop + (event.clientY - startY)));
    if (top + overlay.offsetHeight > actions.top - stageRect.top - 6) {
      left = Math.min(left, Math.max(6, actions.left - stageRect.left - overlay.offsetWidth - 8));
    }
    overlay.style.transform = 'none';
    overlay.style.left = `${left}px`;
    overlay.style.top = `${top}px`;
    store('efb.flightPosition', `${Math.round(left)},${Math.round(top)}`);
  };
  const stop = (event) => {
    handle.removeEventListener('pointermove', move);
    window.removeEventListener('pointerup', stop);
    window.removeEventListener('pointercancel', stop);
    if (event) handle.releasePointerCapture?.(event.pointerId);
  };
  handle.addEventListener('pointerdown', (event) => {
    if (event.target.closest('button')) return;
    const rect = overlay.getBoundingClientRect();
    const stageRect = stage.getBoundingClientRect();
    startX = event.clientX;
    startY = event.clientY;
    originLeft = rect.left - stageRect.left;
    originTop = rect.top - stageRect.top;
    handle.setPointerCapture?.(event.pointerId);
    handle.addEventListener('pointermove', move);
    window.addEventListener('pointerup', stop);
    window.addEventListener('pointercancel', stop);
    event.preventDefault();
  });
}

function setRouteSummary(plan) {
  const summary = $('#simbriefSummary');
  if (!plan) {
    summary.classList.add('hidden');
    setFlightOverlayPlan(null);
    setPlan(null, { status: $('#simbriefStatus')?.textContent ?? '' });
    return;
  }
  summary.classList.remove('hidden');
  const ident = (point) => point?.ident || '—';
  setText('#routeAlternates', (plan.alternates || []).map(ident).filter((value) => value !== '—').join(', ') || '—');
  setText('#routeString', plan.route || '—');
  setText('#routeWaypoints', `${(plan.waypoints || []).length} 个航点${plan.distanceNm ? ` · ${Math.round(plan.distanceNm)} NM` : ''}`);
  setFlightOverlayPlan(plan);
  // The plan page shows everything the OFP carries, including weights, fuel and
  // the per-leg distances/ETAs that the map card has no room for.
  setPlan(plan, { status: $('#simbriefStatus')?.textContent ?? '' });
}

// The 设置 page mirrors what this page knows about the connection, the manual
// folder and the recorded track.
function pushSettingsInfo() {
  setSettingsInfo({
    manuals: manualInfo(),
    source: `${dataSource.isTrain ? 'Train Sim World 6' : 'X-Plane 12'} · ${lastTelemetry?.protocol ?? '—'}`,
    trackPoints: trackPoints.length,
    version: 'v28'
  });
}

function applyPlan(result, refreshed) {
  if (!result.configured) {
    $('#simbriefStatus').textContent = '未配置 SimBrief Pilot ID。请在电脑端“设置…”里填写 Pilot ID 或用户名后重新加载。';
    setRouteSummary(null);
    drawRoute(null);
    return;
  }
  if (!result.available) {
    $('#simbriefStatus').textContent = result.error
      ? `获取失败：${result.error}`
      : '尚未获取航路。点击下方按钮从 SimBrief 拉取最新飞行计划（仅在点击时请求一次）。';
    setRouteSummary(null);
    drawRoute(null);
    return;
  }
  const when = result.fetchedAt ? new Date(result.fetchedAt).toLocaleString('zh-CN', { hour12: false }) : '—';
  $('#simbriefStatus').textContent = refreshed
    ? `已向 SimBrief 请求并归档（${when}）。`
    : `显示已归档的计划（${when}）。`;
  setRouteSummary(result.plan);
  drawRoute(result.plan);
  setRouteVisible($('#routeToggle').checked);
}

async function loadFlightPlan(refresh) {
  const button = $('#simbriefRefreshButton');
  button.disabled = true;
  try {
    const response = await fetch(`/api/flightplan${refresh ? '?refresh=1' : ''}`);
    const result = await response.json();
    applyPlan(result, refresh);
    await loadHistory();
  } catch (error) {
    $('#simbriefStatus').textContent = `无法读取航路数据：${error.message}`;
  } finally {
    button.disabled = false;
  }
}

// SimBrief only hands out the newest plan, so every fetched plan is archived by
// the bridge and can be picked again here without another network request.
async function loadHistory() {
  const select = $('#planSelect');
  try {
    const response = await fetch('/api/flightplan/history');
    if (!response.ok) return;
    const data = await response.json();
    const entries = data.entries || [];
    select.innerHTML = '';
    if (entries.length === 0) {
      $('#planSelectRow').classList.add('hidden');
      return;
    }
    for (const entry of entries) {
      const option = document.createElement('option');
      option.value = entry.id;
      const when = entry.fetchedAt ? new Date(entry.fetchedAt).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false }) : '';
      const count = entry.waypointCount ? `${entry.waypointCount} 航点` : '';
      option.textContent = [entry.label || '未命名计划', count, when].filter(Boolean).join(' · ') + (entry.active ? '（当前）' : '');
      option.selected = Boolean(entry.active);
      select.appendChild(option);
    }
    $('#planSelectRow').classList.remove('hidden');
  } catch { /* the picker is optional; never block the map */ }
}

async function selectPlan(id) {
  try {
    const response = await fetch(`/api/flightplan?select=${encodeURIComponent(id)}`);
    const result = await response.json();
    if (!response.ok) { showToast(result.error || '切换计划失败'); return; }
    applyPlan(result, false);
    await loadHistory();
    showToast('已切换到所选计划');
  } catch (error) {
    showToast(`切换计划失败：${error.message}`);
  }
}

async function openOfp() {
  const view = $('#ofpView');
  view.classList.add('open');
  view.setAttribute('aria-hidden', 'false');
  $('#ofpMeta').textContent = '读取中…';
  $('#ofpText').textContent = '正在读取 OFP…';
  try {
    const response = await fetch('/api/flightplan/ofp');
    const data = await response.json();
    if (!data.available) {
      $('#ofpMeta').textContent = '—';
      $('#ofpText').textContent = '这份计划没有归档 OFP 文本。请先点“获取/刷新航路”重新拉取一次。';
      return;
    }
    const when = data.fetchedAt ? new Date(data.fetchedAt).toLocaleString('zh-CN', { hour12: false }) : '—';
    $('#ofpMeta').textContent = `${data.label || ''} · 拉取于 ${when}（仅用于飞行模拟）`;
    $('#ofpText').textContent = data.text;
    $('#ofpText').scrollTop = 0;
  } catch (error) {
    $('#ofpMeta').textContent = '—';
    $('#ofpText').textContent = `读取失败：${error.message}`;
  }
}

function closeOfp() {
  const view = $('#ofpView');
  view.classList.remove('open');
  view.setAttribute('aria-hidden', 'true');
}

// The OpenWeatherMap key stays on the PC: the tiles are fetched from the bridge,
// which adds the key and caches them. Attribution is added to the map corner.
const WEATHER_ICONS = {
  clouds: '<svg viewBox="0 0 24 24"><path d="M6.5 18h11a4 4 0 0 0 .4-8 5.5 5.5 0 0 0-10.6 1.2A3.4 3.4 0 0 0 6.5 18Z"/></svg>',
  precipitation: '<svg viewBox="0 0 24 24"><path d="M6.5 15h11a4 4 0 0 0 .4-8 5.5 5.5 0 0 0-10.6 1.2A3.4 3.4 0 0 0 6.5 15Z"/><path d="M8.5 18.5 7.5 21M12 18.5 11 21M15.5 18.5 14.5 21"/></svg>',
  wind: '<svg viewBox="0 0 24 24"><path d="M3 8h10.5a2.5 2.5 0 1 0-2.5-2.5M3 12h14.5a2.5 2.5 0 1 1-2.5 2.5M3 16h8"/></svg>',
  temperature: '<svg viewBox="0 0 24 24"><path d="M12 4a2 2 0 0 1 2 2v7a4 4 0 1 1-4 0V6a2 2 0 0 1 2-2Z"/><path d="M12 15V8"/></svg>',
  pressure: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12.5" r="7.5"/><path d="M12 12.5 16.5 9"/></svg>'
};
const WEATHER_MAIN_ICON = '<svg viewBox="0 0 24 24"><path d="M6.5 17h11a4 4 0 0 0 .4-8 5.5 5.5 0 0 0-10.6 1.2A3.4 3.4 0 0 0 6.5 17Z"/><path d="M12 6.5V4M17 7.5l1.5-1.5M7 7.5 5.5 6"/></svg>';

function setupWeather(config) {
  weatherConfig = config ?? null;
  const configured = Boolean(weatherConfig?.configured);
  const host = $('#weatherSubIcons');
  $('#weatherToggle').innerHTML = WEATHER_MAIN_ICON;
  host.innerHTML = '';
  for (const layer of weatherConfig?.layers ?? []) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'weather-icon';
    button.dataset.layer = layer.name;
    button.title = configured ? layer.label : `${layer.label}（未配置 API Key）`;
    button.setAttribute('aria-label', layer.label);
    button.disabled = !configured;
    button.innerHTML = WEATHER_ICONS[layer.name] ?? WEATHER_ICONS.clouds;
    button.addEventListener('click', () => {
      const next = button.classList.contains('active') ? '' : layer.name;
      applyWeatherLayer(next);
    });
    host.appendChild(button);
  }
  if (!configured) {
    const hint = document.createElement('p');
    hint.className = 'weather-hint';
    hint.textContent = '未配置 OpenWeatherMap API Key：在电脑端托盘“设置…”→ 天气 里填写。';
    host.appendChild(hint);
  }
  const saved = readStored('efb.weatherLayer');
  const available = configured && (weatherConfig?.layers ?? []).some((layer) => layer.name === saved);
  applyWeatherLayer(available ? saved : '');
}

function readStored(key) {
  try { return localStorage.getItem(key) ?? ''; } catch { return ''; }
}

function store(key, value) {
  try { localStorage.setItem(key, value); } catch { /* private mode */ }
}

function applyWeatherLayer(name) {
  if (weatherLayer) {
    map.removeLayer(weatherLayer);
    weatherLayer = null;
  }
  store('efb.weatherLayer', name ?? '');
  for (const button of document.querySelectorAll('.weather-icon')) {
    button.classList.toggle('active', button.dataset.layer === name);
  }
  $('#weatherToggle').classList.toggle('active', Boolean(name));
  if (!name || !weatherConfig?.configured) {
    renderAttribution();
    return;
  }
  weatherLayer = L.tileLayer(`/api/weather/tile/${encodeURIComponent(name)}/{z}/{x}/{y}.png`, {
    // The free OpenWeatherMap maps are global forecasts: request them at low
    // zoom and let Leaflet scale them when zoomed in.
    maxNativeZoom: weatherConfig.maxZoom ?? 12,
    maxZoom: 19,
    // The provider's tiles are already quite transparent (alpha ~40-130), so the
    // default has to be high for the overlay to be visible at all.
    opacity: weatherOpacityValue() / 100,
    zIndex: 2,
    attribution: '天气：© <a href="https://openweathermap.org/">OpenWeather</a>'
  });
  weatherLayer.addTo(map);
  renderAttribution();
  scheduleWeatherDataHint(name);
}

function weatherOpacityValue() {
  const stored = Number(readStored('efb.weatherOpacity'));
  return Number.isFinite(stored) && stored >= 30 && stored <= 100 ? stored : 85;
}

function applyWeatherOpacity(percent) {
  store('efb.weatherOpacity', String(percent));
  if (weatherLayer) weatherLayer.setOpacity(percent / 100);
}

// OpenWeatherMap answers with an empty (fully transparent) tile when the layer
// has no data in that area - precipitation over a dry region is the common case.
// The bridge counts those tiles per layer, so the EFB can explain an apparently
// unchanged map instead of leaving the user guessing.
let weatherHintTimer;
let weatherHintLayer = '';

function scheduleWeatherDataHint(layer) {
  clearTimeout(weatherHintTimer);
  weatherHintLayer = layer;
  if (!layer) return;
  weatherHintTimer = setTimeout(() => checkWeatherData(layer), 3500);
}

async function checkWeatherData(layer) {
  if (weatherHintLayer !== layer) return;
  try {
    const response = await fetch('/api/weather/status');
    if (!response.ok) return;
    const data = await response.json();
    const counter = (data.layers || []).find((entry) => entry.name === layer);
    if (!counter || counter.fetched < 3) return;
    if (counter.empty !== counter.fetched) return;
    const label = (weatherConfig?.layers ?? []).find((entry) => entry.name === layer)?.label ?? layer;
    showToast(`${label} 图层在当前区域没有数据（例如现在没有降水），可换 云 / 温度 / 风 试试`);
  } catch { /* the hint is optional */ }
}

async function bootstrap() {
  const config = await loadConfig();
  setupWeather(config.weather);
  groundConfig = config.ground ?? null;
  airlineLogoUrlTemplate = config.airlineLogoUrlTemplate ?? '';
  // The configured source decides the icon, the units and which panels exist before the first frame
  // arrives; the protocol of the frames themselves takes over in updateTelemetry().
  dataSource = { ...sourceInfo('', config.telemetrySource), configured: config.telemetrySource };
  await loadAirlines();
  $('#groundToggle').dataset.wanted = readStored('efb.ground') === '1' ? '1' : '0';
  setGroundEnabled(readStored('efb.ground') === '1');
  $('#weatherOpacity').value = String(weatherOpacityValue());
  if (config.customBaseMap) {
    const customBaseMap = L.tileLayer(config.customBaseMap.url, {
      maxZoom: 19, attribution: config.customBaseMap.attribution
    });
    baseLayers.set('custom', customBaseMap);
    $('#customBaseMapName').textContent = config.customBaseMap.name;
    $('#customBaseMapRow').classList.remove('hidden');
  }
  renderAttribution();
  markBaseLayer();
  layoutRotator();
  initTrack();
  applyAircraftIcon(readStored('efb.aircraftIcon') || 'modern');
  applyNightMode(readStored('efb.nightMode') === '1');
  // Rail + pages first: initTrack() and the map both need to know the final
  // layout, and the manual list is what the 手册 page shows on first open.
  initPages({
    onLayout: () => requestAnimationFrame(() => { layoutRotator(); map.invalidateSize(); }),
    onPageShown: (name) => {
      if (name === 'map') requestAnimationFrame(() => { layoutRotator(); map.invalidateSize(); });
      if (name === 'plan') setPlan(flightPlan, { status: $('#simbriefStatus')?.textContent ?? '' });
      if (name === 'settings') pushSettingsInfo();
    }
  });
  initManual();
  refreshManuals().then(pushSettingsInfo);
  wireFlightDrag();
  const storedPosition = readStored('efb.flightPosition');
  if (storedPosition.includes(',')) {
    const [left, top] = storedPosition.split(',').map(Number);
    if (Number.isFinite(left) && Number.isFinite(top)) {
      $('#flightOverlay').style.transform = 'none';
      $('#flightOverlay').style.left = `${left}px`;
      $('#flightOverlay').style.top = `${top}px`;
    }
  }
  if (readStored('efb.flightCollapsed') === '1') {
    $('#flightOverlay').classList.add('collapsed');
    $('#foCollapseButton').textContent = '▸';
  }
  setFlightOverlay(readStored('efb.flightVisible') !== '0');
  setRouteVisible($('#routeToggle').checked);
  applySourceMode(dataSource);
  setTrainCard(readStored('efb.trainVisible') !== '0');
  connect();
  // A train has no SimBrief plan and no airport ground layout, so neither endpoint is even asked:
  // that also keeps the settings-window "not configured" prompts from appearing for the wrong mode.
  if (!dataSource.isTrain) {
    loadFlightPlan(false);
  } else {
    loadTswStatus();
  }
}

// Reports the source's own health in the diagnostics drawer. The browser cannot reach the game API
// itself (it is on the game machine's loopback), so the bridge is the only witness.
async function loadTswStatus() {
  try {
    const response = await fetch('/api/tsw/status');
    if (!response.ok) return;
    const data = await response.json();
    const status = data.status;
    if (!status) {
      setText('#groundDiagnostic', '—');
      return;
    }
    const endpoints = status.endpoints ?? {};
    const live = [endpoints.position, endpoints.speed, endpoints.driverAid].filter(Boolean);
    setText('#groundDiagnostic', `${status.note || '—'}${live.length > 0 ? `（${live.join(' · ')}）` : ''}`);
  } catch { /* diagnostics only */ }
}

// ---------------------------------------------------------------- ground layout
// apt.dat ground data (runways, taxiway pavements, painted lines, taxi signs,
// gates and taxi routes) is parsed by the bridge and only requested when the map
// is zoomed in far enough, so nothing is loaded while flying at cruise level.
const groundPane = map.createPane('groundPane');
groundPane.style.zIndex = '350';
groundPane.style.pointerEvents = 'none';
const groundRenderer = L.canvas({ pane: 'groundPane', padding: 0.4 });
const groundLayers = [];
let groundLabels = [];
let groundAirport = '';
let groundEnabled = false;
let groundAnchor = null;
let groundData = null;
let groundLabelZoom = 0;
let groundRetryTimer = 0;
let groundNotice = '';
let groundNoticeAt = 0;
let groundLoadedIcao = '';
// How far the aircraft may drift before the ground layout is looked up again.
// The lookup itself uses an 8 NM radius, so half of that is the right
// granularity. (While nmBetween() was mis-scaled this check was effectively
// 86 NM, which is why the layer never followed the aircraft.)
const GROUND_REANCHOR_NM = 4;

function groundConfigured() {
  return Boolean(groundConfig?.configured);
}

function groundButtonTitle() {
  if (!groundConfigured()) return '机场地面图层：还没指定 X-Plane 12 安装目录（电脑端托盘“设置…”→ 地图）';
  return `机场地面图层（缩放 ≥ ${groundConfig.minZoom ?? 15} 显示）`;
}

// The desktop settings window can change the X-Plane folder while this page is
// open, so the configuration is re-read instead of being cached for the session.
async function refreshGroundConfig() {
  try {
    const config = await loadConfig();
    groundConfig = config.ground ?? null;
    const button = $('#groundToggle');
    if (button) button.title = groundButtonTitle();
    if (groundEnabled && groundConfigured()) updateGroundLayer(true);
  } catch { /* keep the previous configuration */ }
}

function notifyGround(message) {
  const now = Date.now();
  if (message === groundNotice && now - groundNoticeAt < 20000) return;
  groundNotice = message;
  groundNoticeAt = now;
  showToast(message);
}

function setGroundDiagnostic(text) {
  const node = $('#groundDiagnostic');
  if (node) node.textContent = text;
}

// The current ground-layer diagnostic text, so switching away from train mode can restore the row
// without waiting for the next map move to recompute it.
function groundDiagnosticText() {
  if (!groundConfigured()) return '未配置 X-Plane 12 安装目录';
  if (!groundEnabled) return '已关闭';
  const minZoom = groundConfig.minZoom ?? 15;
  if (!groundZoomReady()) return `已开启 · 缩放到 ${minZoom} 级后加载（当前 ${Math.round(map.getZoom())}）`;
  if (groundAirport) return `${groundAirport} · ${groundData ? groundSummary(groundData) : '加载中'}`;
  return '已开启 · 正在加载…';
}

function groundSummary(airport) {
  const count = (list) => (list ?? []).length;
  const parts = [];
  if (count(airport.runways)) parts.push(`跑道 ${count(airport.runways)}`);
  if (count(airport.pavements)) parts.push(`铺面 ${count(airport.pavements)}`);
  if (count(airport.lines)) parts.push(`标线 ${count(airport.lines)}`);
  if (count(airport.signs)) parts.push(`标牌 ${count(airport.signs)}`);
  if (count(airport.parking)) parts.push(`机位 ${count(airport.parking)}`);
  return parts.join(' · ') || '该机场没有可绘制的要素';
}

function setGroundEnabled(enabled) {
  // The user's preference is stored separately from the rendered state, because train mode forces
  // the layer off (airport taxiways are not railways). Without this, entering train mode would
  // silently overwrite the preference and the layer would stay off after switching back.
  const button = $('#groundToggle');
  if (button) button.dataset.wanted = enabled ? '1' : '0';
  store('efb.ground', enabled ? '1' : '0');
  groundEnabled = enabled && !dataSource.isTrain;
  if (button) {
    button.classList.toggle('active', groundEnabled);
    button.setAttribute('aria-pressed', String(groundEnabled));
    // Kept clickable on purpose: pressing it re-reads the configuration, so a
    // folder picked in the desktop settings window takes effect immediately.
    button.disabled = false;
    button.title = groundButtonTitle();
  }
  if (!groundEnabled)
  {
    clearGround();
    setGroundDiagnostic('已关闭');
    return;
  }
  const minZoom = groundConfig?.minZoom ?? 15;
  if (!groundConfigured())
  {
    setGroundDiagnostic('未配置 X-Plane 12 安装目录');
    refreshGroundConfig();
  }
  // Enabling it while zoomed out used to do nothing at all, which reads as
  // "the layer is broken" - say what is missing instead.
  else if (map.getZoom() < minZoom)
  {
    setGroundDiagnostic(`已开启 · 缩放到 ${minZoom} 级后加载（当前 ${Math.round(map.getZoom())}）`);
    notifyGround(`地面图层要缩放到 ${minZoom} 级以上才会显示（当前 ${Math.round(map.getZoom())}）`);
  }
  else setGroundDiagnostic('已开启 · 正在加载…');
  updateGroundLayer(true);
}

function clearGround() {
  clearTimeout(groundRetryTimer);
  groundRetryTimer = 0;
  for (const layer of groundLayers) if (map.hasLayer(layer)) map.removeLayer(layer);
  groundLayers.length = 0;
  for (const label of groundLabels) if (map.hasLayer(label)) map.removeLayer(label);
  groundLabels = [];
  groundAirport = '';
  groundData = null;
  groundLabelZoom = 0;
  groundLoadedIcao = '';
  setGroundDiagnostic(groundEnabled
    ? (groundConfigured() ? `已开启 · 缩放到 ${groundConfig.minZoom ?? 15} 级后加载` : '未配置 X-Plane 12 安装目录')
    : '已关闭');
}

function groundZoomReady() {
  return groundEnabled && groundConfigured() && map.getZoom() >= (groundConfig.minZoom ?? 15);
}

function updateGroundLayer(force = false) {
  if (!groundEnabled) return;
  if (!groundConfigured()) {
    clearGround();
    notifyGround('还没指定 X-Plane 12 安装目录：电脑端托盘“设置…”→ 地图 → 选择文件夹');
    return;
  }
  if (!groundZoomReady()) {
    if (groundLayers.length > 0 || groundLabels.length > 0) clearGround();
    return;
  }
  const reference = Number.isFinite(lastTelemetry?.latitude) && Number.isFinite(lastTelemetry?.longitude)
    ? [lastTelemetry.latitude, lastTelemetry.longitude]
    : [map.getCenter().lat, map.getCenter().lng];
  if (!force && groundAnchor && nmBetween(groundAnchor, reference) < GROUND_REANCHOR_NM && groundAirport) {
    // Same airport: only the gate/sign labels depend on the zoom level, so
    // rebuild those when the zoom changed and skip the network round trip.
    if (map.getZoom() !== groundLabelZoom && groundData) {
      groundLabelZoom = map.getZoom();
      for (const label of groundLabels) if (map.hasLayer(label)) map.removeLayer(label);
      groundLabels = [];
      addGroundLabels(groundData);
    }
    return;
  }
  groundAnchor = reference;
  const query = `lat=${reference[0].toFixed(5)}&lon=${reference[1].toFixed(5)}&radius=8`;
  fetch(`/api/ground/nearest?${query}`)
    .then((response) => (response.ok ? response.json() : null))
    .then((data) => {
      if (!data) return;
      if (!data.configured) {
        clearGround();
        notifyGround(data.error ?? '还没指定 X-Plane 12 安装目录');
        return;
      }
      if (data.error) {
        clearGround();
        notifyGround(`${data.error}（电脑端“设置…”→ 地图 可检查目录）`);
        return;
      }
      // The first lookup triggers the airport index build on the PC; apt.dat can
      // be tens of megabytes, so poll instead of leaving the layer silently empty.
      if (data.building) {
        notifyGround('正在建立机场索引（首次需要读 apt.dat，请稍等几秒）…');
        clearTimeout(groundRetryTimer);
        groundRetryTimer = setTimeout(() => updateGroundLayer(true), 2500);
        return;
      }
      const airport = data.airport;
      if (!airport) {
        clearGround();
        notifyGround(`附近 ${8} 海里内没有机场数据`);
        return;
      }
      if (airport.icao === groundAirport && !force) return;
      drawGround(airport);
      if (groundLoadedIcao !== groundAirport) {
        groundLoadedIcao = groundAirport;
        showToast(`${airport.icao ?? '机场'} 地面数据已加载：${groundSummary(airport)}`);
      }
    })
    .catch(() => { });
}

function drawGround(airport) {
  clearGround();
  groundAirport = airport.icao ?? '';
  groundData = airport;
  groundLabelZoom = map.getZoom();
  setGroundDiagnostic(`${airport.icao ?? '机场'} · ${groundSummary(airport)}`);
  const add = (layer) => { layer.addTo(map); groundLayers.push(layer); };

  // Deliberately distinct from the OpenStreetMap airport rendering underneath it
  // (which already draws pale taxiways and its own gate lettering): blue-grey
  // aprons with a bright edge, yellow painted markings and a dark-cased runway
  // make it obvious at a glance which lines came from apt.dat.
  for (const pavement of airport.pavements ?? []) {
    if ((pavement.points ?? []).length < 3) continue;
    add(L.polygon(pavement.points, {
      renderer: groundRenderer, pane: 'groundPane', interactive: false,
      color: mapPalette.ground.pavement.edge, weight: 1.6, opacity: .95,
      fillColor: mapPalette.ground.pavement.fill, fillOpacity: mapPalette.ground.pavement.fillOpacity
    }));
  }
  for (const route of airport.routes ?? []) {
    if ((route.points ?? []).length < 2) continue;
    add(L.polyline(route.points, {
      renderer: groundRenderer, pane: 'groundPane', interactive: false,
      color: mapPalette.ground.route, weight: 1.6, opacity: .45, dashArray: '3 7'
    }));
  }
  for (const line of airport.lines ?? []) {
    if ((line.points ?? []).length < 2) continue;
    add(L.polyline(line.points, {
      renderer: groundRenderer, pane: 'groundPane', interactive: false,
      color: mapPalette.ground.marking, weight: Math.max(2, Math.min(4, map.getZoom() - 13)), opacity: .95
    }));
  }
  for (const runway of airport.runways ?? []) {
    if (!runway.a || !runway.b) continue;
    const weight = Math.max(6, Math.min(20, (runway.widthM ?? 45) / 5.5));
    add(L.polyline([runway.a, runway.b], {
      renderer: groundRenderer, pane: 'groundPane', interactive: false,
      color: mapPalette.ground.runwayCase, weight: weight + 2.5, opacity: .55
    }));
    add(L.polyline([runway.a, runway.b], {
      renderer: groundRenderer, pane: 'groundPane', interactive: false,
      color: mapPalette.ground.runway, weight, opacity: .95
    }));
    add(L.polyline([runway.a, runway.b], {
      renderer: groundRenderer, pane: 'groundPane', interactive: false,
      color: mapPalette.ground.runwayMark, weight: Math.max(1.6, weight / 7), opacity: .92, dashArray: '7 9'
    }));
  }
  for (const parking of airport.parking ?? []) {
    add(L.circleMarker([parking.lat, parking.lon], {
      renderer: groundRenderer, pane: 'groundPane', interactive: false,
      radius: 3.5, color: mapPalette.ground.standRing, weight: 1, fillColor: mapPalette.ground.stand, fillOpacity: 1
    }));
  }
  addGroundLabels(airport);
}

// Gate names and taxiway sign texts, capped so a huge airport cannot flood the
// screen; both are static, so they are only built once per airport.
function addGroundLabels(airport) {
  const zoom = map.getZoom();
  const canShow = zoom >= (groundConfig?.minZoom ?? 15) + 1;
  if (!canShow) return;
  const centre = map.getCenter();
  const candidates = [];
  for (const parking of airport.parking ?? []) {
    if (parking.name) candidates.push({ text: parking.name, lat: parking.lat, lon: parking.lon, kind: 'gate' });
  }
  for (const sign of airport.signs ?? []) {
    if (sign.text) candidates.push({ text: sign.text, lat: sign.lat, lon: sign.lon, kind: 'sign' });
  }
  candidates.sort((a, b) => nmBetween([centre.lat, centre.lng], [a.lat, a.lon]) - nmBetween([centre.lat, centre.lng], [b.lat, b.lon]));
  for (const candidate of candidates.slice(0, 60)) {
    const label = L.marker([candidate.lat, candidate.lon], {
      interactive: false,
      keyboard: false,
      icon: L.divIcon({
        className: 'ground-label',
        iconSize: [0, 0],
        html: `<span class="${candidate.kind}">${escapeHtml(candidate.text)}</span>`
      })
    });
    label.addTo(map);
    groundLabels.push(label);
  }
}

setInterval(() => {
  if (!lastTelemetry) return;
  const seconds = Math.max(0, Math.floor((Date.now() - lastTelemetry.receivedAt) / 1000));
  $('#ageDiagnostic').textContent = `${seconds} 秒前`;
  if (seconds > 3 && socket?.readyState === WebSocket.OPEN) setStatus('lost', statusText('lost', dataSource.isTrain));
}, 1000);

if ('serviceWorker' in navigator && window.isSecureContext) navigator.serviceWorker.register('/sw.js').catch(() => {});
bootstrap().catch((error) => {
  // Never fail silently: a broken start-up used to leave the page looking alive
  // but unresponsive, with no hint about which call went wrong.
  console.error('EFB 启动失败', error);
  showToast('无法读取应用配置');
});
