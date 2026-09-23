// The left rail and the three non-map pages (手册 / 计划 / 设置).
//
// Collapsed the rail is the logo plus four page icons; expanded it carries the
// functional interface of the page that is open, while the main area shows that
// page's primary view.

const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => [...document.querySelectorAll(selector)];
const PAGES = ['map', 'manual', 'plan', 'settings'];
const COMPACT = () => window.matchMedia('(max-width: 900px)').matches;
const read = (key) => { try { return localStorage.getItem(key) ?? ''; } catch { return ''; } };
const write = (key, value) => { try { localStorage.setItem(key, value); } catch { /* private mode */ } };

let page = 'map';
let hooks = {};

export function currentPage() {
  return page;
}

function applyVisibility() {
  const open = document.body.classList.contains('rail-open');
  for (const panel of $$('[data-page-panel]')) {
    // The map stays mounted (Leaflet must not be re-created), everything else is
    // simply hidden while another page is in front.
    panel.classList.toggle('hidden', panel.dataset.pagePanel !== page);
  }
  for (const pane of $$('.rail-pane')) {
    pane.classList.toggle('hidden', pane.dataset.pane !== page);
  }
  for (const item of $$('.rail-item')) {
    const active = item.dataset.page === page;
    item.classList.toggle('active', active);
    if (active) item.setAttribute('aria-current', 'page');
    else item.removeAttribute('aria-current');
  }
  const toggle = $('#railToggle');
  toggle.setAttribute('aria-expanded', String(open));
  toggle.textContent = open ? '‹' : '›';
  $('#scrim').classList.toggle('open', open && COMPACT());
  document.body.dataset.page = page;
}

export function setRailOpen(open, { persist = true } = {}) {
  document.body.classList.toggle('rail-open', open);
  if (persist) write('efb.railOpen', open ? '1' : '0');
  applyVisibility();
  hooks.onLayout?.();
}

export function showPage(name, { persist = true, open = true } = {}) {
  if (!PAGES.includes(name)) return;
  page = name;
  if (open) document.body.classList.add('rail-open');
  if (persist) {
    write('efb.page', name);
    if (open) write('efb.railOpen', '1');
  }
  applyVisibility();
  hooks.onPageShown?.(name);
  hooks.onLayout?.();
  // On a phone or a portrait iPad the rail is an overlay: leaving it open would
  // squeeze the page the user just asked for, so it gets out of the way.
  if (open && COMPACT()) setRailOpen(false);
}

export function initPages(options = {}) {
  hooks = options;
  for (const item of $$('.rail-item')) {
    item.addEventListener('click', () => {
      // Tapping the icon of the page you are already on toggles the rail.
      if (item.dataset.page === page && document.body.classList.contains('rail-open') && !COMPACT()) {
        setRailOpen(false);
        return;
      }
      showPage(item.dataset.page);
    });
  }
  $('#railToggle').addEventListener('click', () => {
    setRailOpen(!document.body.classList.contains('rail-open'));
  });
  $('#menuButton').addEventListener('click', () => {
    setRailOpen(!document.body.classList.contains('rail-open'));
  });
  $('#scrim').addEventListener('click', () => setRailOpen(false));
  window.matchMedia('(max-width: 900px)').addEventListener('change', (event) => {
    // Rotating into portrait (or dragging the window narrow) turns the rail into
    // an overlay; keeping it open there would cover the page it is showing.
    if (event.matches) document.body.classList.remove('rail-open');
    applyVisibility();
  });

  const storedPage = read('efb.page');
  page = PAGES.includes(storedPage) ? storedPage : 'map';
  // Narrow screens always start collapsed: the rail is an overlay there, and
  // opening the app on a covered map is worse than one extra tap.
  const open = read('efb.railOpen') !== '0' && !COMPACT();
  document.body.classList.toggle('rail-open', open);
  applyVisibility();
}

// ------------------------------------------------------------------ 计划页
const isNumber = (value) => typeof value === 'number' && Number.isFinite(value);

function formatNumber(value, digits = 0) {
  return isNumber(value) ? value.toLocaleString('en-US', { maximumFractionDigits: digits }) : '—';
}

function clockFrom(ms) {
  if (!isNumber(ms)) return '—';
  const date = new Date(ms);
  return `${String(date.getUTCHours()).padStart(2, '0')}:${String(date.getUTCMinutes()).padStart(2, '0')}Z`;
}

function duration(seconds) {
  if (!isNumber(seconds) || seconds <= 0) return '—';
  const total = Math.round(seconds / 60);
  return `${Math.floor(total / 60)}h${String(total % 60).padStart(2, '0')}m`;
}

function haversineNm(a, b) {
  const rad = Math.PI / 180;
  const dLat = (b.lat - a.lat) * rad;
  const dLon = (b.lon - a.lon) * rad;
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(a.lat * rad) * Math.cos(b.lat * rad) * Math.sin(dLon / 2) ** 2;
  return 2 * 3440.065 * Math.asin(Math.min(1, Math.sqrt(h)));
}

function card(label, labelEn, value, unit) {
  const node = document.createElement('div');
  node.className = 'plan-card';
  node.innerHTML = '<i></i><b></b><u></u>';
  node.querySelector('i').textContent = labelEn ? `${label} ${labelEn}` : label;
  node.querySelector('b').textContent = value;
  node.querySelector('u').textContent = unit ?? '';
  return node;
}

/**
 * One row of the load & fuel card. Chinese label, English label, value, and an
 * optional hover/focus tooltip (used for the aircraft's structural limits).
 */
function loadRow({ zh, en, value, unit = '', note = '', tip = '', key = false }) {
  const row = document.createElement('div');
  row.className = key ? 'plan-load-row key' : 'plan-load-row';
  const label = document.createElement('span');
  label.className = 'plan-load-label';
  const zhNode = document.createElement('b');
  zhNode.textContent = zh;
  const enNode = document.createElement('i');
  enNode.textContent = en;
  label.append(zhNode, enNode);
  const valueNode = document.createElement('b');
  valueNode.className = 'plan-load-value';
  valueNode.textContent = isNumber(value) ? `${formatNumber(value)}${unit ? ` ${unit}` : ''}` : '—';
  row.append(label, valueNode);
  if (note) {
    const noteNode = document.createElement('u');
    noteNode.className = 'plan-load-note';
    noteNode.textContent = note;
    row.appendChild(noteNode);
  }
  if (tip) {
    // data-tip drives the styled tooltip; aria-label keeps it available to
    // screen readers and on touch (a tap sets :hover in iOS Safari).
    row.dataset.tip = tip;
    row.tabIndex = 0;
    row.setAttribute('aria-label', `${zh} ${en} ${valueNode.textContent}。${tip}`);
  }
  return row;
}

function loadSection(zh, en) {
  const node = document.createElement('h3');
  node.className = 'plan-load-section';
  node.innerHTML = '<span></span><i></i>';
  node.querySelector('span').textContent = zh;
  node.querySelector('i').textContent = en;
  return node;
}

/** Every waypoint of the plan with distance, ETA and coordinates. */
function buildLegs(plan) {
  const points = [];
  // SimBrief repeats the departure and arrival airports in the navlog, so the
  // same fix can appear twice in a row; keep the table free of those duplicates.
  const push = (point) => {
    const last = points[points.length - 1];
    if (last && last.ident === point.ident
      && Math.abs(last.lat - point.lat) < 1e-6 && Math.abs(last.lon - point.lon) < 1e-6) return;
    points.push(point);
  };
  if (isNumber(plan.origin?.lat)) push({ ...plan.origin, kind: 'origin' });
  for (const waypoint of plan.waypoints ?? []) {
    if (isNumber(waypoint.lat) && isNumber(waypoint.lon)) push(waypoint);
  }
  if (isNumber(plan.destination?.lat)) push({ ...plan.destination, kind: 'destination' });
  let cumulative = 0;
  const legs = points.map((point, index) => {
    if (index > 0) cumulative += haversineNm(points[index - 1], point);
    return { ...point, index, cumulative };
  });
  const total = cumulative || plan.distanceNm || 0;
  const departure = Date.parse(plan.flight?.estimatedOff || plan.flight?.plannedOff || '');
  const enroute = Number.isFinite(plan.eteSeconds) ? plan.eteSeconds
    : Number.isFinite(plan.flight?.estimatedEnrouteSeconds) ? plan.flight.estimatedEnrouteSeconds
      : Number.isFinite(plan.flight?.plannedEnrouteSeconds) ? plan.flight.plannedEnrouteSeconds : null;
  for (const leg of legs) {
    leg.eta = Number.isFinite(departure) && isNumber(enroute) && total > 0
      ? clockFrom(departure + (leg.cumulative / total) * enroute * 1000)
      : '—';
    leg.legDistance = leg.index === 0 ? 0 : leg.cumulative - legs[leg.index - 1].cumulative;
  }
  return { legs, total };
}

export function setPlan(plan, meta = {}) {
  const title = $('#planTitle');
  const subtitle = $('#planSubtitle');
  const times = $('#planTimes');
  const cards = $('#planCards');
  const table = $('#planTable');
  const load = $('#planLoad');
  const extra = $('#planExtra');
  const empty = $('#planEmpty');
  times.innerHTML = '';
  cards.innerHTML = '';
  table.innerHTML = '';
  load.innerHTML = '';
  extra.innerHTML = '';

  if (!plan) {
    title.textContent = '飞行计划';
    subtitle.textContent = meta.status || '尚未获取航路。';
    empty.classList.remove('hidden');
    $('#planLegCount').textContent = '';
    $('#planUnits').textContent = '';
    return;
  }
  empty.classList.add('hidden');
  const flight = plan.flight ?? {};
  const origin = plan.origin?.ident ?? '—';
  const destination = plan.destination?.ident ?? '—';
  title.textContent = `${origin} → ${destination}`;
  subtitle.textContent = [
    flight.callsign,
    [flight.aircraftName, flight.aircraft].filter(Boolean).join(' '),
    flight.registration,
    plan.route
  ].filter(Boolean).join(' · ') || '已归档的 SimBrief 计划';

  const overnight = (a, b) => (String(a).slice(0, 10) !== String(b).slice(0, 10) ? ' +1' : '');
  const block = document.createElement('div');
  block.className = 'plan-time';
  block.innerHTML = `
    <span><i>计划离港</i><b>${plan.flight?.plannedOff ? String(plan.flight.plannedOff).slice(11, 16) : '—'}Z</b></span>
    <span><i>预计到达</i><b>${plan.flight?.estimatedOn ? String(plan.flight.estimatedOn).slice(11, 16) : '—'}Z${plan.flight?.estimatedOn ? overnight(plan.flight.plannedOn, plan.flight.estimatedOn) : ''}</b></span>
    <span><i>计划到达</i><b>${plan.flight?.plannedOn ? String(plan.flight.plannedOn).slice(11, 16) : '—'}Z${plan.flight?.plannedOn ? overnight(plan.flight.plannedOff, plan.flight.plannedOn) : ''}</b></span>
    <span><i>航路时间</i><b>${duration(plan.eteSeconds ?? plan.flight?.estimatedEnrouteSeconds ?? plan.flight?.plannedEnrouteSeconds)}</b></span>`;
  times.appendChild(block);

  const { legs, total } = buildLegs(plan);
  cards.appendChild(card('航路距离', 'Route Dist.', formatNumber(plan.distanceNm ?? total), 'NM'));
  cards.appendChild(card('巡航高度', 'Cruise Alt.', isNumber(plan.cruiseAltitudeFt) ? `FL${Math.round(plan.cruiseAltitudeFt / 100)}` : '—', isNumber(plan.cruiseAltitudeFt) ? `${formatNumber(plan.cruiseAltitudeFt)} ft` : ''));
  cards.appendChild(card('巡航', 'Cruise', isNumber(plan.cruise?.mach) ? `M${plan.cruise.mach.toFixed(2)}` : '—', isNumber(plan.cruise?.tas) ? `${formatNumber(plan.cruise.tas)} kt TAS` : ''));
  cards.appendChild(card('航点数', 'Waypoints', String((plan.waypoints ?? []).length), `${legs.length} 个航段`));
  cards.appendChild(card('备降', 'Alternates', (plan.alternates ?? []).map((item) => item.ident).filter(Boolean).join(' / ') || '—', ''));
  cards.appendChild(card('大圆距离', 'Great Circle', formatNumber(plan.cruise?.greatCircleNm), 'NM'));

  const header = document.createElement('thead');
  header.innerHTML = '<tr><th>#</th><th>航点</th><th>航路</th><th>高度</th><th>距离</th><th>累计/ETA</th><th>坐标</th></tr>';
  table.appendChild(header);
  const body = document.createElement('tbody');
  legs.forEach((leg, index) => {
    const row = document.createElement('tr');
    if (leg.kind) row.className = leg.kind;
    const cells = [
      String(index + 1),
      leg.ident || leg.name || '—',
      leg.via || (leg.kind === 'origin' ? '起飞机场' : leg.kind === 'destination' ? '降落机场' : '—'),
      isNumber(leg.altitudeFt) ? `FL${Math.round(leg.altitudeFt / 100)}` : '—',
      index === 0 ? '—' : `${Math.round(leg.legDistance)} NM`,
      `${Math.round(leg.cumulative)} NM · ${leg.eta}`,
      `${leg.lat.toFixed(3)}, ${leg.lon.toFixed(3)}`
    ];
    row.innerHTML = cells.map((value, cellIndex) => `<td${cellIndex > 4 ? ' class="num"' : ''}></td>`).join('');
    cells.forEach((value, cellIndex) => { row.children[cellIndex].textContent = value; });
    body.appendChild(row);
  });
  table.appendChild(body);
  $('#planLegCount').textContent = `${legs.length} 个航段 · ${formatNumber(total)} NM`;

  const units = plan.units === 'LBS' ? 'lb' : 'kg';
  $('#planUnits').textContent = plan.units ? `单位 ${units}` : '';
  const weight = plan.weight ?? {};
  const fuel = plan.fuel ?? {};
  const type = plan.flight?.aircraftName || plan.flight?.aircraft || '';
  // The structural limits belong on hover: the row itself stays a clean number.
  const limitTip = (label, labelEn, value) => (isNumber(value)
    ? `${label} ${labelEn} · ${formatNumber(value)} ${units}${type ? `（${type}）` : ''}`
    : '');

  load.appendChild(loadSection('重量', 'Weight'));
  load.appendChild(loadRow({ zh: '乘客', en: 'Passengers', value: weight.pax, unit: '人 / pax' }));
  load.appendChild(loadRow({ zh: '行李', en: 'Bags', value: weight.bags, unit: '件 / pcs' }));
  load.appendChild(loadRow({ zh: '货邮', en: 'Cargo', value: weight.cargo, unit: units }));
  load.appendChild(loadRow({ zh: '业载', en: 'Payload', value: weight.payload, unit: units }));
  load.appendChild(loadRow({ zh: '使用空重', en: 'OEW (Operating Empty Weight)', value: weight.oew, unit: units }));
  load.appendChild(loadRow({
    zh: '零油重量', en: 'ZFW (Zero Fuel Weight)', value: weight.zfw, unit: units, key: true,
    tip: limitTip('最大零油重量', 'MZFW', weight.maxZfw)
  }));
  load.appendChild(loadRow({
    zh: '起飞重量', en: 'TOW (Take-off Weight)', value: weight.tow, unit: units, key: true,
    tip: limitTip('最大起飞重量', 'MTOW', weight.maxTow)
  }));
  load.appendChild(loadRow({
    zh: '停机坪重量', en: 'Ramp Weight', value: weight.ramp, unit: units
  }));
  load.appendChild(loadRow({
    zh: '着陆重量', en: 'LW (Landing Weight)', value: weight.lw, unit: units, key: true,
    tip: limitTip('最大着陆重量', 'MLW', weight.maxLw)
  }));

  load.appendChild(loadSection('燃油', 'Fuel'));
  load.appendChild(loadRow({ zh: '起飞油量', en: 'Block Fuel', value: fuel.block, unit: units }));
  load.appendChild(loadRow({ zh: '松刹车油量', en: 'Take-off Fuel', value: fuel.takeoff, unit: units }));
  load.appendChild(loadRow({ zh: '着陆剩余', en: 'Landing Fuel', value: fuel.landing, unit: units }));
  load.appendChild(loadRow({ zh: '滑行油量', en: 'Taxi Fuel', value: fuel.taxi, unit: units }));
  load.appendChild(loadRow({ zh: '航段耗油', en: 'Trip Fuel', value: fuel.enroute, unit: units }));
  load.appendChild(loadRow({ zh: '备份油', en: 'Contingency Fuel', value: fuel.contingency, unit: units }));
  load.appendChild(loadRow({ zh: '备降油', en: 'Alternate Fuel', value: fuel.alternate, unit: units }));
  load.appendChild(loadRow({ zh: '最终储备', en: 'Final Reserve', value: fuel.reserve, unit: units }));
  load.appendChild(loadRow({ zh: '额外油', en: 'Extra Fuel', value: fuel.extra, unit: units }));
  load.appendChild(loadRow({ zh: '最低起飞油量', en: 'Min Take-off Fuel', value: fuel.minTakeoff, unit: units }));
  load.appendChild(loadRow({ zh: '平均流量', en: 'Avg Fuel Flow', value: fuel.avgFlow, unit: `${units}/h` }));

  extra.appendChild(loadSection('巡航', 'Cruise'));
  extra.appendChild(loadRow({ zh: '成本指数', en: 'Cost Index', value: plan.cruise?.costIndex }));
  extra.appendChild(loadRow({ zh: '空中距离', en: 'Air Distance', value: plan.cruise?.airDistanceNm, unit: 'NM' }));
  extra.appendChild(loadRow({ zh: '大圆距离', en: 'Great Circle Distance', value: plan.cruise?.greatCircleNm, unit: 'NM' }));
  extra.appendChild(loadRow({
    zh: '计划生成', en: 'Generated',
    value: null,
    note: plan.flight?.generatedAt ? `${String(plan.flight.generatedAt).replace('T', ' ').slice(0, 16)}Z` : ''
  }));
  extra.appendChild(loadSection('备降', 'Alternates'));
  for (const alternate of plan.alternates ?? []) {
    extra.appendChild(loadRow({
      zh: alternate.ident || '备降机场',
      en: alternate.name || 'Alternate',
      value: null,
      note: [alternate.runway ? `跑道 ${alternate.runway}` : '', alternate.ident].filter(Boolean).join(' · ')
    }));
  }
}

// ------------------------------------------------------------------ 设置页
export function setSettingsInfo({ manuals, source, trackPoints, version } = {}) {
  if (manuals) {
    $('#manualFolderInfo').textContent = manuals.configured
      ? (manuals.folder || '已配置')
      : (manuals.error || '未配置');
    $('#manualCountInfo').textContent = manuals.configured ? `${manuals.count} 本${manuals.opened ? ` · 当前 ${manuals.opened}` : ''}` : '—';
  }
  if (source) $('#sourceNameInfo').textContent = source;
  if (Number.isFinite(trackPoints)) $('#trackPointsInfo').textContent = `${trackPoints.toLocaleString('en-US')} 个点`;
  if (version) $('#appVersionInfo').textContent = version;
}
