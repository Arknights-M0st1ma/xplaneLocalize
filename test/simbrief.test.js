import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { createFlightPlanCache, extractOfpText, parseCoordinate, parseFlightPlan, simbriefEndpoint } from '../src/simbrief.js';

const fixture = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'simbrief-ofp.json'), 'utf8'));

test('uses userid for a numeric Pilot ID and username otherwise', () => {
  assert.equal(simbriefEndpoint('123456', 'https://example.test/fetcher'), 'https://example.test/fetcher?userid=123456&json=v2');
  assert.equal(simbriefEndpoint('some pilot', 'https://example.test/fetcher'), 'https://example.test/fetcher?username=some%20pilot&json=v2');
  assert.throws(() => simbriefEndpoint(''));
});

test('parses decimal, signed and hemisphere coordinates', () => {
  assert.equal(parseCoordinate('31.143400'), 31.1434);
  assert.equal(parseCoordinate(-0.4542), -0.4542);
  assert.equal(parseCoordinate('N51.470000'), 51.47);
  assert.equal(parseCoordinate('W000.460000'), -0.46);
  assert.equal(parseCoordinate('51.4700S'), -51.47);
  assert.equal(parseCoordinate(''), null);
  assert.equal(parseCoordinate('abc'), null);
  assert.equal(parseCoordinate(undefined), null);
});

test('normalises a SimBrief OFP for the map', () => {
  const plan = parseFlightPlan(fixture);
  assert.equal(plan.origin.ident, 'ZSPD');
  assert.equal(plan.origin.lat, 31.1434);
  assert.equal(plan.destination.ident, 'RJAA');
  assert.equal(plan.destination.lon, 140.3864);
  assert.equal(plan.alternates.length, 2);
  assert.deepEqual(plan.alternates.map((item) => item.ident), ['RJTT', 'ZSSS']);
  assert.equal(plan.route, 'AND BS G455 ...');
  // general.aircraft_icao is empty in real responses; aircraft.icaocode carries it.
  assert.equal(plan.aircraft, 'A346');
  assert.equal(plan.distanceNm, 832);
  assert.equal(plan.cruiseAltitudeFt, 35000);
  assert.equal(plan.eteSeconds, 11 * 3600 + 12 * 60 + 53);
  assert.equal(plan.flight.callsign, 'ZZZ/1234');
  assert.equal(plan.flight.aircraft, 'A346');
  assert.equal(plan.flight.aircraftName, 'A340-600');
  assert.equal(plan.flight.registration, 'N634SB');
  assert.equal(plan.flight.plannedOff, '2026-09-10T22:55:00Z');
  assert.equal(plan.flight.plannedOn, '2026-09-11T08:42:00Z');
  assert.equal(plan.flight.plannedEnrouteSeconds, 9 * 3600 + 47 * 60);
  assert.equal(plan.flight.estimatedOn, '2026-09-11T10:07:53Z');
  assert.equal(plan.flight.generatedAt, '2026-09-10T22:01:06Z');
  // The 0/0 placeholder fix is dropped, the five real fixes remain.
  assert.equal(plan.waypoints.length, 6);
  assert.equal(plan.waypoints[2].via, 'G455');
  assert.equal(plan.waypoints[3].type, 'vor');
  assert.equal(plan.waypoints[4].type, 'ndb');
});

test('falls back to navlog fixes when an airport has no position', () => {
  const plan = parseFlightPlan({ origin: { icao_code: 'ZSPD' }, destination: { icao_code: 'RJAA' }, navlog: { fix: fixture.navlog.fix.slice(1, 6) } });
  assert.equal(plan.origin.lat, 31.45);
  assert.equal(plan.destination.lat, 35.7647);
});

test('accepts the flat json=v2 navlog array', () => {
  // json=v2 sends navlog as a flat array of fixes instead of navlog.fix.
  const plan = parseFlightPlan({
    general: { route: 'SULU4L SULUS Z650 VEMUT', aircraft_icao: 'A346', route_distance: '5607', initial_altitude: '31000' },
    origin: { icao_code: 'EDDF', pos_lat: '50.033306', pos_long: '8.570456' },
    destination: { icao_code: 'VHHH', pos_lat: '22.308889', pos_long: '113.914722' },
    alternate: [{ icao_code: 'ZGGG', pos_lat: '23.393333', pos_long: '113.308333' }],
    navlog: [
      { ident: 'DF101', type: 'wpt', pos_lat: '49.781692', pos_long: '8.541486', via_airway: 'SULU4L', altitude_feet: '6900', stage: 'CLB' },
      { ident: 'COSJE', type: 'wpt', pos_lat: '49.717531', pos_long: '9.947000', via_airway: 'SULU4L' },
      { ident: 'VHHH', type: 'apt', pos_lat: '22.308889', pos_long: '113.914722', stage: 'DSC' }
    ]
  });
  assert.equal(plan.waypoints.length, 3);
  assert.equal(plan.origin.ident, 'EDDF');
  assert.equal(plan.destination.ident, 'VHHH');
  assert.equal(plan.waypoints[0].via, 'SULU4L');
  assert.equal(plan.waypoints[0].altitudeFt, 6900);
  assert.equal(plan.waypoints[2].type, 'apt');
  assert.equal(plan.distanceNm, 5607);
  assert.equal(plan.alternates[0].ident, 'ZGGG');
});

test('survives an empty navlog string', () => {
  const plan = parseFlightPlan({
    general: { route: 'AND BS G455' },
    origin: { icao_code: 'ZSPD', pos_lat: '31.1434', pos_long: '121.8052' },
    destination: { icao_code: 'RJAA', pos_lat: '35.7647', pos_long: '140.3864' },
    navlog: ''
  });
  assert.equal(plan.waypoints.length, 0);
  assert.equal(plan.route, 'AND BS G455');
  assert.equal(plan.origin.ident, 'ZSPD');
});

test('returns null when a payload holds no usable route', () => {
  assert.equal(parseFlightPlan(null), null);
  assert.equal(parseFlightPlan({ fetch: { status: 'Error: Unknown UserID' } }), null);
  assert.equal(parseFlightPlan({ origin: { icao_code: 'ZSPD', pos_lat: '0', pos_long: '0' } }), null);
});

test('cache never calls SimBrief unless a refresh is requested', async () => {
  const calls = [];
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (url) => {
    calls.push(String(url));
    return new Response(JSON.stringify(fixture), { status: 200, headers: { 'content-type': 'application/json' } });
  };
  try {
    const cache = createFlightPlanCache({ user: '123456' });
    assert.equal(cache.status().available, false);
    assert.deepEqual(await cache.get(), cache.status());
    assert.equal(calls.length, 0);

    const loaded = await cache.get({ refresh: true });
    assert.equal(loaded.available, true);
    assert.equal(loaded.plan.origin.ident, 'ZSPD');
    assert.equal(calls.length, 1);

    // Cached reads do not touch the network again.
    await cache.get();
    assert.equal(calls.length, 1);

    // A second immediate refresh is rate limited instead of hammering SimBrief.
    const throttled = await cache.get({ refresh: true });
    assert.equal(calls.length, 1);
    assert.equal(throttled.available, true);
    assert.match(throttled.error, /过于频繁/);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('cache reports SimBrief errors without throwing', async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () => new Response(JSON.stringify({ fetch: { status: 'Error: Unknown UserID' } }), { status: 400 });
  try {
    const cache = createFlightPlanCache({ user: '999999999' });
    const result = await cache.get({ refresh: true });
    assert.equal(result.available, false);
    assert.equal(result.error, 'Unknown UserID');
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('extracts the OFP as plain text without any markup', () => {
  const text = extractOfpText(fixture);
  assert.match(text, /\[ OFP \] EDDF-VHHH/);
  assert.match(text, /page 1\/4/);
  assert.match(text, /起降性能报告/);
  assert.match(text, /TAKEOFF AND LANDING REPORT/);
  assert.ok(!text.includes('<'), '不要保留 HTML 标签');
  assert.ok(!text.includes('BKMK'), '不要保留注释');
});

test('archives every fetched plan and can switch back to an earlier one', async () => {
  const calls = [];
  const originalFetch = globalThis.fetch;
  let requestId = '100';
  globalThis.fetch = async (url) => {
    calls.push(String(url));
    const body = JSON.parse(JSON.stringify(fixture));
    body.params = { request_id: requestId, sequence_id: 'seq', user_id: '123456' };
    body.origin = { ...body.origin, icao_code: requestId === '100' ? 'EDDF' : 'ZSPD' };
    return new Response(JSON.stringify(body), { status: 200 });
  };
  try {
    const cache = createFlightPlanCache({ user: '123456', minIntervalMs: 0 });
    const first = await cache.get({ refresh: true });
    assert.equal(first.plan.origin.ident, 'EDDF');
    assert.equal(first.historyCount, 1);

    requestId = '200';
    const second = await cache.get({ refresh: true });
    assert.equal(second.plan.origin.ident, 'ZSPD');
    assert.equal(second.historyCount, 2);

    const history = cache.history();
    assert.equal(history.length, 2);
    assert.equal(history[0].active, true);
    assert.equal(history[1].label.startsWith('EDDF →'), true);

    // Switching back is local only: no extra request to SimBrief.
    assert.equal(cache.select(history[1].id), true);
    assert.equal(calls.length, 2);
    const restored = cache.status();
    assert.equal(restored.plan.origin.ident, 'EDDF');
    assert.equal(restored.activeId, history[1].id);
    assert.equal(cache.ofp().available, true);

    assert.equal(cache.select('does-not-exist'), false);
  } finally {
    globalThis.fetch = originalFetch;
  }
});
