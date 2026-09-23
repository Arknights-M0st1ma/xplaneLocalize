// SimBrief OFP reader.
//
// Endpoint documented at https://developers.navigraph.com/docs/simbrief/fetching-ofp-data
// and in the pinned forum topic https://forum.navigraph.com/t/fetching-a-users-latest-ofp-data/5297
//
// - Reading a pilot's latest OFP needs no API key; only a Pilot ID or the
//   Navigraph alias is required.
// - Official rules: call this only in response to a user action and never poll
//   it, or the server firewall may ban the client. Every caller in this project
//   therefore caches the result and only fetches again when the user asks.

const DEFAULT_ENDPOINT = 'https://www.simbrief.com/api/xml.fetcher.php';
const LATITUDE_KEYS = ['pos_lat', 'lat', 'latitude'];
const LONGITUDE_KEYS = ['pos_long', 'lon', 'lng', 'longitude'];

export function simbriefEndpoint(user, endpoint = DEFAULT_ENDPOINT) {
  const value = String(user ?? '').trim();
  if (!value) throw new Error('未配置 SimBrief Pilot ID 或用户名');
  const parameter = /^\d{1,7}$/.test(value) ? 'userid' : 'username';
  return `${endpoint}?${parameter}=${encodeURIComponent(value)}&json=v2`;
}

// SimBrief returns positions as decimal numbers, but older payloads and the
// XML variant also use hemisphere prefixes such as "N51.470000".
export function parseCoordinate(value) {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value !== 'string') return null;
  const text = value.trim();
  if (!text) return null;
  const plain = Number(text);
  if (Number.isFinite(plain)) return plain;
  const match = /^([NSEW])?\s*(-?\d+(?:\.\d+)?)\s*([NSEW])?$/i.exec(text);
  if (!match) return null;
  const hemisphere = (match[1] || match[3] || '').toUpperCase();
  const degrees = Number(match[2]);
  if (!Number.isFinite(degrees)) return null;
  return hemisphere === 'S' || hemisphere === 'W' ? -Math.abs(degrees) : degrees;
}

function asArray(value) {
  if (value === null || value === undefined) return [];
  return Array.isArray(value) ? value : [value];
}

// json=v2 returns the fixes as a flat array under "navlog"; the older shape
// (and the XML variant) nests them as navlog.fix. Both are accepted.
function navlogFixes(payload) {
  const navlog = payload?.navlog;
  if (Array.isArray(navlog)) return navlog;
  return asArray(navlog?.fix);
}

// The complete OFP arrives as one large <pre> block inside text.plan_html, plus
// a plain text takeoff/landing report. Plain text keeps the EFB viewer safe (no
// remote markup is ever inserted into the page).
function htmlToText(html) {
  if (!html) return '';
  return String(html)
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<\/?(p|div|tr|table|pre|h[1-6])[^>]*>/gi, '\n')
    .replace(/<!--[\s\S]*?-->/g, '')
    .replace(/<[^>]+>/g, '')
    .replace(/&nbsp;/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/[ \t]+\n/g, '\n')
    .replace(/\n{4,}/g, '\n\n\n')
    .trim();
}

export function extractOfpText(payload) {
  const text = payload?.text ?? {};
  const plan = htmlToText(text.plan_html);
  const tlr = String(text.tlr_section ?? '').trim();
  const combined = tlr.length > 0 ? `${plan}\n\n───── 起降性能报告 ─────\n\n${tlr}` : plan;
  const limit = 900000;
  return combined.length <= limit ? combined : `${combined.slice(0, limit)}\n…（内容过长，已截断）`;
}

function firstCoordinate(entry, keys) {
  if (!entry || typeof entry !== 'object') return null;
  for (const key of keys) {
    const value = parseCoordinate(entry[key]);
    if (value !== null) return value;
  }
  return null;
}

function toNumber(value) {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value !== 'string') return null;
  const parsed = Number(value.trim());
  return Number.isFinite(parsed) ? parsed : null;
}

function waypointType(raw) {
  const value = String(raw ?? '').toLowerCase();
  if (value.startsWith('vor')) return 'vor';
  if (value.startsWith('ndb')) return 'ndb';
  if (value === 'apt' || value === 'airport' || value === 'ad') return 'apt';
  return 'wpt';
}

function parseWaypoint(entry) {
  const lat = firstCoordinate(entry, LATITUDE_KEYS);
  const lon = firstCoordinate(entry, LONGITUDE_KEYS);
  if (lat === null || lon === null) return null;
  // A 0/0 placeholder would stretch the route across the whole world.
  if (lat === 0 && lon === 0) return null;
  const ident = String(entry.ident ?? entry.name ?? '').trim();
  if (!ident) return null;
  return {
    ident,
    name: String(entry.name ?? '').trim(),
    type: waypointType(entry.type),
    via: String(entry.via_airway ?? entry.via ?? '').trim(),
    altitudeFt: toNumber(entry.altitude_feet ?? entry.altitude),
    stage: String(entry.stage ?? '').trim(),
    lat,
    lon
  };
}

function parseAirport(entry) {
  if (!entry || typeof entry !== 'object') return null;
  const ident = String(entry.icao_code ?? entry.icao ?? entry.iata_code ?? entry.ident ?? '').trim().toUpperCase();
  if (!ident) return null;
  return {
    ident,
    name: String(entry.name ?? '').trim(),
    runway: String(entry.plan_rwy ?? entry.runway ?? '').trim(),
    lat: firstCoordinate(entry, LATITUDE_KEYS),
    lon: firstCoordinate(entry, LONGITUDE_KEYS)
  };
}

// SimBrief sends durations as "HH:MM:SS" (and times as ISO-8601 UTC strings).
function firstText(...values) {
  for (const value of values) {
    const text = String(value ?? '').trim();
    if (text.length > 0) return text;
  }
  return '';
}

function durationSeconds(value) {
  if (value === null || value === undefined) return null;
  const text = String(value).trim();
  if (!text) return null;
  if (!text.includes(':')) {
    const plain = Number(text);
    return Number.isFinite(plain) ? plain : null;
  }
  let seconds = 0;
  for (const part of text.split(':')) {
    const piece = Number(part);
    if (!Number.isFinite(piece)) return null;
    seconds = seconds * 60 + piece;
  }
  return seconds;
}

// Everything the flight card in the EFB shows.
function flightInfo(payload) {
  const general = payload?.general ?? {};
  const times = payload?.times ?? {};
  const aircraft = payload?.aircraft ?? {};
  const airline = String(general.icao_airline ?? '').trim();
  const number = String(general.flight_number ?? '').trim();
  return {
    callsign: airline ? (number ? `${airline}/${number}` : airline) : number,
    airline,
    number,
    aircraft: firstText(general.aircraft_icao, aircraft.icaocode),
    aircraftName: String(aircraft.name ?? '').trim(),
    registration: String(aircraft.reg ?? '').trim(),
    plannedOff: String(times.sched_off ?? ''),
    plannedOn: String(times.sched_on ?? ''),
    plannedEnrouteSeconds: durationSeconds(times.sched_time_enroute),
    estimatedOff: String(times.est_off ?? ''),
    estimatedOn: String(times.est_on ?? ''),
    estimatedEnrouteSeconds: durationSeconds(times.est_time_enroute),
    generatedAt: String(payload?.params?.time_generated ?? '')
  };
}

function withFallbackPosition(airport, waypoint) {
  if (!airport) return null;
  // SimBrief uses 0/0 for airports without a position; that is not a real place.
  if (airport.lat === 0 && airport.lon === 0) airport = { ...airport, lat: null, lon: null };
  if (airport.lat !== null && airport.lon !== null) return airport;
  if (!waypoint) return airport;
  return { ...airport, lat: waypoint.lat, lon: waypoint.lon };
}

// Weights and fuel for the plan page.
//
// The names matter: a real json=v2 OFP puts the *planned* weights under
// est_zfw / est_tow / est_ldw and the aircraft limits under max_zfw / max_tow /
// max_ldw. Reading zfw/tow/ldw/mtow/mlw (an earlier guess here) returned null for
// every pilot, which is why the load card showed nothing. The shorter names are
// still accepted as a fallback for other payload shapes.
function planWeights(payload) {
  const weights = payload?.weights ?? {};
  const fuel = payload?.fuel ?? {};
  const general = payload?.general ?? {};
  const pick = (...keys) => {
    for (const key of keys) {
      const value = toNumber(weights[key]);
      if (value !== null) return value;
    }
    return null;
  };
  return {
    // Units live on the request parameters ("kgs" / "lbs"), not in the blocks.
    units: String(payload?.params?.units ?? weights.units ?? fuel.units ?? '').trim().toUpperCase(),
    weight: {
      pax: toNumber(weights.pax_count),
      bags: toNumber(weights.bag_count),
      cargo: toNumber(weights.cargo),
      freight: toNumber(weights.freight_added),
      payload: toNumber(weights.payload),
      oew: toNumber(weights.oew),
      zfw: pick('est_zfw', 'zfw'),
      tow: pick('est_tow', 'tow'),
      lw: pick('est_ldw', 'ldw'),
      ramp: pick('est_ramp', 'ramp'),
      maxZfw: pick('max_zfw', 'mzfw'),
      maxTow: pick('max_tow', 'mtow'),
      maxLw: pick('max_ldw', 'mlw'),
      towLimitCode: String(weights.tow_limit_code ?? '').trim()
    },
    fuel: {
      block: toNumber(fuel.plan_ramp),
      takeoff: toNumber(fuel.plan_takeoff),
      landing: toNumber(fuel.plan_landing),
      taxi: toNumber(fuel.taxi),
      enroute: toNumber(fuel.enroute_burn),
      contingency: toNumber(fuel.contingency),
      alternate: toNumber(fuel.alternate_burn),
      reserve: toNumber(fuel.reserve),
      extra: toNumber(fuel.extra),
      minTakeoff: toNumber(fuel.min_takeoff),
      avgFlow: toNumber(fuel.avg_fuel_flow)
    },
    cruise: {
      mach: toNumber(general.cruise_mach),
      tas: toNumber(general.cruise_tas),
      costIndex: toNumber(general.costindex ?? general.cost_index),
      airDistanceNm: toNumber(general.air_distance),
      greatCircleNm: toNumber(general.gc_distance)
    }
  };
}

// Normalises an OFP payload into the small shape the EFB draws. Unknown fields
// are ignored rather than throwing, so a SimBrief format change degrades to a
// partial route instead of breaking the map.
export function parseFlightPlan(payload) {
  if (!payload || typeof payload !== 'object') return null;
  const general = payload.general ?? {};
  const waypoints = navlogFixes(payload).map(parseWaypoint).filter(Boolean);
  const alternates = [
    ...asArray(payload.alternate).map(parseAirport),
    ...asArray(payload.takeoff_altn).map(parseAirport)
  ].filter(Boolean);

  const origin = withFallbackPosition(parseAirport(payload.origin), waypoints[0]);
  const destination = withFallbackPosition(parseAirport(payload.destination), waypoints[waypoints.length - 1]);
  // A payload with no drawable geometry at all is reported as unavailable
  // instead of producing an empty overlay.
  const positioned = (point) => Boolean(point) && point.lat !== null && point.lon !== null;
  if (waypoints.length === 0 && !positioned(origin) && !positioned(destination)) return null;

  return {
    origin,
    destination,
    alternates,
    route: String(general.route ?? general.route_ifps ?? '').trim(),
    aircraft: firstText(general.aircraft_icao, payload.aircraft?.icaocode),
    cruiseAltitudeFt: toNumber(general.initial_altitude ?? general.cruise_altitude),
    distanceNm: toNumber(general.route_distance ?? general.gc_distance),
    eteSeconds: durationSeconds(payload.times?.est_time_enroute),
    ...planWeights(payload),
    flight: flightInfo(payload),
    waypoints
  };
}

export function createFlightPlanCache({ user, endpoint = DEFAULT_ENDPOINT, minIntervalMs = 30000, now = () => Date.now() } = {}) {
  // Every successfully fetched plan is kept so the EFB can switch back to an
  // earlier one: SimBrief's API only ever returns the most recent plan.
  const entries = [];
  let activeId = null;
  let error = null;
  let lastAttempt = 0;
  let inFlight = null;

  const configured = Boolean(String(user ?? '').trim());
  const owner = String(user ?? '').trim().toLowerCase();
  const active = () => entries.find((entry) => entry.id === activeId) ?? null;
  const label = (plan) => {
    const origin = plan?.origin?.ident ?? '?';
    const destination = plan?.destination?.ident ?? '?';
    const distance = plan?.distanceNm ? ` · ${Math.round(plan.distanceNm)} NM` : '';
    return `${origin} → ${destination}${distance}`;
  };

  function archive(parsed, ofpText, requestId) {
    const id = requestId && entries.find((entry) => entry.requestId === requestId)?.id
      ? entries.find((entry) => entry.requestId === requestId).id
      : Math.random().toString(16).slice(2, 14);
    const existing = entries.findIndex((entry) => entry.id === id);
    if (existing >= 0) entries.splice(existing, 1);
    entries.unshift({ id, owner, requestId: requestId ?? '', fetchedAt: now(), label: label(parsed), plan: parsed, ofpText });
    while (entries.length > 12) entries.pop();
    activeId = id;
  }

  async function fetchPlan() {
    const url = simbriefEndpoint(user, endpoint);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 15000);
    try {
      const response = await fetch(url, { signal: controller.signal, headers: { accept: 'application/json' } });
      const text = await response.text();
      let payload;
      try {
        payload = JSON.parse(text);
      } catch {
        throw new Error(`SimBrief 返回了无法解析的响应（HTTP ${response.status}）`);
      }
      if (!response.ok) {
        const detail = payload?.fetch?.status || `HTTP ${response.status}`;
        throw new Error(String(detail).replace(/^Error:\s*/, ''));
      }
      const parsed = parseFlightPlan(payload);
      if (!parsed) throw new Error('SimBrief 响应中没有可用的航路数据');
      return { parsed, ofpText: extractOfpText(payload), requestId: String(payload?.params?.request_id ?? '') };
    } catch (cause) {
      if (cause?.name === 'AbortError') throw new Error('SimBrief 请求超时');
      throw cause;
    } finally {
      clearTimeout(timer);
    }
  }

  async function refresh() {
    const elapsed = now() - lastAttempt;
    if (elapsed < minIntervalMs) {
      const wait = Math.ceil((minIntervalMs - elapsed) / 1000);
      throw new Error(`请求过于频繁，请 ${wait} 秒后重试`);
    }
    lastAttempt = now();
    const { parsed, ofpText, requestId } = await fetchPlan();
    archive(parsed, ofpText, requestId);
    error = null;
    return parsed;
  }

  return {
    configured,
    status() {
      const current = active();
      return {
        configured,
        available: Boolean(current),
        fetchedAt: current?.fetchedAt ?? null,
        error,
        plan: current?.plan ?? null,
        activeId,
        historyCount: entries.filter((entry) => entry.owner === owner).length
      };
    },
    history() {
      return entries.filter((entry) => entry.owner === owner).map((entry) => ({
        id: entry.id,
        fetchedAt: entry.fetchedAt,
        label: entry.label,
        origin: entry.plan?.origin?.ident ?? null,
        destination: entry.plan?.destination?.ident ?? null,
        aircraft: entry.plan?.aircraft ?? '',
        distanceNm: entry.plan?.distanceNm ?? null,
        waypointCount: entry.plan?.waypoints?.length ?? 0,
        hasOfp: entry.ofpText.length > 0,
        active: entry.id === activeId
      }));
    },
    select(id) {
      const match = entries.find((entry) => entry.id === id && entry.owner === owner);
      if (!match) return false;
      activeId = match.id;
      return true;
    },
    ofp() {
      const current = active();
      return {
        available: Boolean(current?.ofpText),
        label: current?.label ?? '',
        fetchedAt: current?.fetchedAt ?? null,
        text: current?.ofpText ?? ''
      };
    },
    async get({ refresh: force = false } = {}) {
      if (!configured) return this.status();
      if (!force) return this.status();
      if (!inFlight) {
        inFlight = refresh()
          .catch((cause) => { error = cause.message; return null; })
          .finally(() => { inFlight = null; });
      }
      await inFlight;
      return this.status();
    }
  };
}
