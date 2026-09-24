// test/tsw-fake-api.js
//
// Fake Train Sim World 6 game API for tests and manual local probing.
//
// This file is a TEST DOUBLE for a THIRD-PARTY GAME API. It is not part of our
// product and it is not the game: every route, header name and field name below
// comes from third-party reverse-engineered documentation (see
// docs/TSW6-TELEMETRY.md sections 3.1-3.4 and the appendix 9 field checklist),
// and none of it has been confirmed against a running TSW6. A mismatch therefore
// means "our documentation is wrong", not "the game is wrong". Never confuse
// this double with the real game at http://127.0.0.1:31270, and never point a
// production build at it.
//
// Typical test use (the factory is async because it awaits 'listening', so
// baseUrl/port are already valid when the promise resolves -- there is no
// separate `ready` promise to await):
//
//   import { createFakeTswApi } from './tsw-fake-api.js';
//   const fake = await createFakeTswApi();
//   fake.baseUrl;   // http://127.0.0.1:<ephemeral port>
//   fake.key;       // the DTGCommKey value this fake accepts
//   await fake.stop();
//
// Manual probing (prints the URL and the expected key, then serves the 'ok'
// scenario until Ctrl+C; tries 31270 like the game and falls back to an
// ephemeral port when that is taken):
//
//   node test/tsw-fake-api.js
//   node test/tsw-fake-api.js 40000
//   node test/tsw-fake-api.js 31270 subscription-only   # position only in the subscription
//   $env:EFB_TSW_KEY='my-key'; node test/tsw-fake-api.js
//
// Scenarios: ok | no-info | bad-key | error-envelope | slow | minimal | subscription-only | offline

import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * The value the game uses for "undefined" numeric fields (float max).
 * Our client must drop these instead of treating them as 3.4e38 metres.
 */
export const TSW_SENTINEL = 3.4028e38;

/** Key the fake accepts when the caller does not supply one. */
export const DEFAULT_KEY = 'DTG-FAKE-KEY-0123456789ABCDEF';

/** Port the real game is documented to listen on, used by the manual runner. */
export const DEFAULT_PORT = 31270;

/** Default delay for the 'slow' scenario, long enough to trip a 1-3 s timeout. */
export const DEFAULT_SLOW_DELAY_MS = 3000;

export const SCENARIO_NAMES = Object.freeze([
  'ok',
  'no-info',
  'bad-key',
  'error-envelope',
  'slow',
  'minimal',
  'offline'
]);

/** Auth failure body copied verbatim from the reverse-engineered spec. */
export const INVALID_KEY_BODY = Object.freeze({
  errorCode: 'dtg.comm.InvalidKey',
  errorMessage: 'API Key for request doesn\'t match CommAPIKey.txt in the game config directory.'
});

/** The one subscription fact the spec confirms: an unknown id is a 400. */
export const NO_SUCH_SUBSCRIPTION_BODY = Object.freeze({
  errorCode: 'dtg.comm.NoSuchSubscription',
  errorMessage: 'Could not find requested subscription ID'
});

// The two bodies below are INVENTED for the fake. The brief only confirms the
// subscription error, but a client still needs a way to tell "this route does
// not exist" from "my key is stale", and our C# client logs that difference.
const NO_SUCH_ROUTE_BODY = Object.freeze({
  errorCode: 'dtg.comm.NoSuchRoute',
  errorMessage: 'Could not find requested route'
});

const NO_SUCH_ENDPOINT_BODY = Object.freeze({
  errorCode: 'dtg.comm.NoSuchEndpoint',
  errorMessage: 'Could not find requested subscription endpoint'
});

const NOT_IMPLEMENTED_BODY = Object.freeze({
  errorCode: 'dtg.comm.NotImplemented',
  errorMessage: 'The fake TSW API only implements GET routes'
});

// Scenario profiles. Every scenario is a small delta from 'ok', which keeps the
// hostile scenarios (bad key, error envelope, hostile payloads) from drifting
// away from the healthy one.
const SCENARIOS = Object.freeze({
  // Everything healthy: /info present, all telemetry endpoints answering.
  ok: { info: true, payload: 'normal' },
  // /info 404s so our client's fallback probing can be exercised.
  'no-info': { info: false, payload: 'normal' },
  // Every route rejects, even with the right key: a rotated/stale key.
  'bad-key': { info: true, payload: 'normal', rejectEverything: true },
  // HTTP 200 with {"Result":"Error"}: the payload that punishes clients which
  // trust the status code instead of the Result field.
  'error-envelope': { info: true, payload: 'normal', envelopeError: true },
  // Valid responses, but delayed, so a client timeout can fire.
  slow: { info: true, payload: 'normal', delayMs: DEFAULT_SLOW_DELAY_MS },
  // Success envelopes holding renamed/missing fields and 3.4028e+38 sentinels.
  minimal: { info: true, payload: 'minimal' },
  // /get/DriverAid.PlayerInfo answers, but only the subscription carries the position — the shape
  // the two public TSW6 clients work around by reading /subscription instead of /get.
  'subscription-only': { info: true, payload: 'subscriptionOnly' },
  // The listener is closed, so connections are refused.
  offline: { info: false, payload: 'normal', closed: true }
});

// Node tree used by /list and /list/{node}. Node paths in /list use '/'
// separators (the brief gives DriverInput/Wiper_F as an example) while /get
// joins the node with dots.
const NODE_TREE = Object.freeze({
  CurrentDrivableActor: { nodes: ['Function'], endpoints: ['LatLon'] },
  'CurrentDrivableActor/Function': { nodes: [], endpoints: ['HUD_GetSpeed'] },
  CurrentFormation: { nodes: [], endpoints: ['FormationLength', 'ObjectClass'] },
  DriverAid: { nodes: ['PlayerInfo', 'Data'], endpoints: ['IsActive'] },
  DriverInput: { nodes: ['Wiper_F'], endpoints: [] },
  'DriverInput/Wiper_F': { nodes: [], endpoints: [{ name: 'Value', writable: true }] },
  TimeOfDay: { nodes: [], endpoints: ['data'] }
});

// /get paths the fake answers. The keys are the exact dotted "Node.Endpoint"
// strings a client asks for, including the awkward "Speed (ms)" family.
const ENDPOINT_PATHS = Object.freeze([
  'DriverAid.PlayerInfo',
  'DriverAid.Data',
  'CurrentDrivableActor.Function.HUD_GetSpeed',
  'CurrentFormation.FormationLength',
  'TimeOfDay.data'
]);

function defaultState() {
  return {
    latitude: 51.4704,
    longitude: -0.4593,
    metresPerSecond: 23.42,
    currentTile: { x: 128450, y: 87321 },
    currentServiceName: '1A23 London Paddington',
    playerProfileName: 'FakeDriver',
    driverAid: {
      speedLimit: { value: 22.35 },
      nextSpeedLimit: { value: 13.41 },
      trackMaxSpeed: { value: 44.7 },
      serviceMaxSpeed: { value: 44.7 },
      formationMaxSpeed: { value: 55.56 },
      distanceToNextSpeedLimit: 1234.5,
      distanceToSignal: 456.7,
      gradient: -0.012,
      signalAspectClass: 'Clear',
      signalSeen: true,
      speedLimitSeen: true,
      nextSpeedLimits: [{ distance: 1234.5, value: 13.41 }],
      nextSignals: [{ distance: 456.7, aspect: 'Clear' }]
    }
  };
}

function trimSlashes(value) {
  return value.replace(/^\/+/, '').replace(/\/+$/, '');
}

function normalizeEndpoint(endpoint) {
  return typeof endpoint === 'string'
    ? { Name: endpoint, Writable: false }
    : { Name: endpoint.name, Writable: Boolean(endpoint.writable) };
}

// Values written by hand because speedLimit and friends are nested {value}
// objects while the distance fields are bare numbers -- the asymmetry is in the
// third-party docs and is exactly the kind of thing a parser trips over.
function playerInfoValues(state, payload) {
  if (payload === 'subscriptionOnly') {
    // Same facts as the normal payload, minus the position: this is the shape where a client that
    // only reads /get sees a healthy API and still cannot draw a map.
    return {
      currentTile: { x: state.currentTile.x, y: state.currentTile.y },
      currentServiceName: state.currentServiceName,
      playerProfileName: state.playerProfileName
    };
  }
  if (payload === 'minimal') {
    // Hostile shape: no geoLocation at all, coordinates renamed to lat/lon, and
    // the useless game-internal tile grid as a sentinel.
    return {
      lat: state.latitude,
      lon: state.longitude,
      currentTile: { x: TSW_SENTINEL, y: TSW_SENTINEL },
      currentServiceName: '',
      playerProfileName: state.playerProfileName
    };
  }
  return {
    geoLocation: { latitude: state.latitude, longitude: state.longitude },
    currentTile: { x: state.currentTile.x, y: state.currentTile.y },
    currentServiceName: state.currentServiceName,
    playerProfileName: state.playerProfileName
  };
}

function hudSpeedValues(state, payload) {
  // The documented key literally contains a space and parentheses. Some builds
  // are reported to expose plain "Speed" instead, which is what minimal mimics.
  return payload === 'minimal'
    ? { Speed: state.metresPerSecond }
    : { 'Speed (ms)': state.metresPerSecond };
}

function driverAidValues(state, payload) {
  if (payload === 'minimal') {
    return {
      speedLimit: { value: TSW_SENTINEL },
      nextSpeedLimit: { value: TSW_SENTINEL },
      trackMaxSpeed: { value: TSW_SENTINEL },
      serviceMaxSpeed: { value: TSW_SENTINEL },
      formationMaxSpeed: { value: TSW_SENTINEL },
      distanceToNextSpeedLimit: TSW_SENTINEL,
      // distanceToSignal and the look-ahead arrays are absent in this shape.
      gradient: 0,
      signalAspectClass: '',
      signalSeen: false,
      speedLimitSeen: false
    };
  }
  return structuredClone(state.driverAid);
}

function infoBody() {
  return {
    Meta: {
      Worker: 'DTGCommWorkerRC',
      GameName: 'Train Sim World 6®',
      GameBuildNumber: 20251113,
      APIVersion: '1.5',
      GameInstanceID: '00000000-0000-0000-0000-000000000000'
    },
    HttpRoutes: ['/info', '/list', '/list/{node}', '/get/{node}.{endpoint}', '/subscription']
  };
}

function listRootBody() {
  return {
    Result: 'Success',
    NodeName: 'root',
    NodePath: '',
    Nodes: Object.keys(NODE_TREE)
      .filter((node) => !node.includes('/'))
      .map((name) => ({ Name: name }))
  };
}

function sendJson(res, status, body, record) {
  if (record) record.status = status;
  if (res.writableEnded || res.destroyed) return;
  const payload = Buffer.from(JSON.stringify(body), 'utf8');
  // The fake always closes the connection: a real API would keep it alive, but
  // deterministic socket teardown is what lets a test assert that stop() really
  // released the port (and that a client's keep-alive pool is not the reason).
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': payload.length,
    connection: 'close'
  });
  res.end(payload);
}

function mergeValues(target, patch) {
  for (const [key, value] of Object.entries(patch)) {
    const isPlainObject = value !== null && typeof value === 'object' && !Array.isArray(value);
    if (isPlainObject && target[key] !== null && typeof target[key] === 'object' && !Array.isArray(target[key])) {
      mergeValues(target[key], value);
    } else {
      target[key] = value;
    }
  }
  return target;
}

/**
 * Start a fake TSW6 game API listening on 127.0.0.1.
 *
 * @param {object} [options]
 * @param {number} [options.port=0] Port to bind; 0 (default) asks the OS for an
 *   ephemeral port, which is what tests should use so they never collide.
 * @param {boolean} [options.fallbackToEphemeral=false] When the requested port
 *   is taken, bind an ephemeral port instead of throwing (the manual runner
 *   wants this; tests should not, because a silent port change hides bugs).
 * @param {string} [options.host='127.0.0.1'] Interface to bind.
 * @param {string} [options.key] The DTGCommKey value to accept. Falls back to
 *   EFB_TSW_KEY, then DEFAULT_KEY.
 * @param {number} [options.maxRequests=1000] Cap on the recorded request log.
 * @param {number} [options.slowDelayMs] Delay used by the 'slow' scenario.
 * @param {string} [options.scenario='ok'] Scenario to start in.
 * @returns {Promise<object>} the fake handle (see the getters/methods below).
 */
export async function createFakeTswApi(options = {}) {
  const host = options.host ?? '127.0.0.1';
  const requestedPort = options.port ?? 0;
  const fallbackToEphemeral = options.fallbackToEphemeral ?? false;
  const expectedKey = options.key ?? process.env.EFB_TSW_KEY ?? DEFAULT_KEY;
  const maxRequests = options.maxRequests ?? 1000;
  const slowDelayMs = options.slowDelayMs ?? DEFAULT_SLOW_DELAY_MS;

  const state = defaultState();
  const requests = [];
  const counts = Object.create(null);
  const sockets = new Set();
  const timers = new Map();
  // Subscription id -> registered paths. Empty until a client registers one.
  const subscriptions = new Map();

  let scenario = 'ok';
  let delayMs = 0;
  let stopped = false;
  let lastError = null;
  let port = requestedPort;

  const telemetryBuilders = {
    'DriverAid.PlayerInfo': (payload) => playerInfoValues(state, payload),
    'DriverAid.Data': (payload) => driverAidValues(state, payload),
    'CurrentDrivableActor.Function.HUD_GetSpeed': (payload) => hudSpeedValues(state, payload)
  };
  // The other paths exist so /list can advertise them; our client does not read
  // them yet, but a developer probing the fake should still get a Success.
  const endpointBuilders = Object.fromEntries(
    ENDPOINT_PATHS.map((endpointPath) => [endpointPath, telemetryBuilders[endpointPath] ?? (() => ({ value: 0 }))])
  );

  // A pending delay must be resolvable on stop(), otherwise a test that stops
  // the fake during the 'slow' scenario would leave a dangling timer.
  function delay(ms) {
    if (!(ms > 0)) return Promise.resolve();
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        timers.delete(timer);
        resolve();
      }, ms);
      timers.set(timer, resolve);
    });
  }

  function countFor(pathname) {
    return counts[pathname] ?? 0;
  }

  function routeGet(target, res, record, payload) {
    // Node names themselves contain dots (CurrentDrivableActor.Function), so the
    // endpoint name is whatever follows the LAST dot.
    const split = target.lastIndexOf('.');
    if (split <= 0 || split === target.length - 1) {
      sendJson(res, 400, { ...NO_SUCH_ENDPOINT_BODY, errorMessage: `${NO_SUCH_ENDPOINT_BODY.errorMessage}: ${target}` }, record);
      return;
    }
    const builder = endpointBuilders[target];
    if (!builder) {
      sendJson(res, 400, { ...NO_SUCH_ENDPOINT_BODY, errorMessage: `${NO_SUCH_ENDPOINT_BODY.errorMessage}: ${target}` }, record);
      return;
    }
    sendJson(res, 200, { Result: 'Success', Values: builder(payload) }, record);
  }

  function route(pathname, method, requestUrl, res, record, profile) {
    if (pathname === '/info') {
      if (!profile.info) {
        // /info is NOT in the reverse-engineered spec (it ends with a TODO), so
        // this 404 is the realistic "route missing" case our client must survive.
        sendJson(res, 404, { ...NO_SUCH_ROUTE_BODY, errorMessage: `${NO_SUCH_ROUTE_BODY.errorMessage}: ${pathname}` }, record);
        return;
      }
      sendJson(res, 200, infoBody(), record);
      return;
    }
    if (pathname === '/list' || pathname === '/list/') {
      sendJson(res, 200, listRootBody(), record);
      return;
    }
    if (pathname.startsWith('/list/')) {
      const node = trimSlashes(pathname.slice('/list/'.length));
      const entry = NODE_TREE[node];
      if (!entry) {
        sendJson(res, 404, { ...NO_SUCH_ROUTE_BODY, errorMessage: `${NO_SUCH_ROUTE_BODY.errorMessage}: ${pathname}` }, record);
        return;
      }
      sendJson(res, 200, {
        Result: 'Success',
        NodePath: node,
        NodeName: node.split('/').pop(),
        Nodes: entry.nodes.map((name) => ({ Name: name })),
        Endpoints: entry.endpoints.map(normalizeEndpoint)
      }, record);
      return;
    }
    if (pathname.startsWith('/get/')) {
      routeGet(trimSlashes(pathname.slice('/get/'.length)), res, record, profile.payload);
      return;
    }
    if (pathname === '/listsubscriptions') {
      sendJson(res, 200, { Result: 'Success', Subscriptions: [...subscriptions.keys()] }, record);
      return;
    }
    if (pathname === '/subscription' || pathname === '/subscription/') {
      // Reads and removes a whole subscription id. Both spellings appear in the wild: the public
      // clients read "/subscription?Subscription=N" and remove "/subscription/?Subscription=N".
      const id = Number.parseInt(requestUrl.searchParams.get('Subscription') ?? '', 10);
      const registered = Number.isInteger(id) ? subscriptions.get(id) : undefined;
      if (method === 'DELETE') {
        subscriptions.delete(id);
        sendJson(res, 200, { Result: 'Success' }, record);
        return;
      }
      if (!registered) {
        // An unknown id is the one subscription fact the spec confirms.
        sendJson(res, 400, NO_SUCH_SUBSCRIPTION_BODY, record);
        return;
      }
      sendJson(res, 200, {
        RequestedSubscriptionID: id,
        Entries: registered.map((path) => ({
          // 'subscription-only' is the profile where /get hides the position: the subscription is
          // exactly where that profile puts it back, so the envelope always serves the full payload.
          Values: endpointBuilders[path]
            ? endpointBuilders[path](profile.payload === 'subscriptionOnly' ? 'normal' : profile.payload)
            : {}
        }))
      }, record);
      return;
    }
    if (pathname.startsWith('/subscription/')) {
      // Registers or removes one path on a subscription id.
      const id = Number.parseInt(requestUrl.searchParams.get('Subscription') ?? '', 10);
      if (!Number.isInteger(id)) {
        sendJson(res, 400, NO_SUCH_SUBSCRIPTION_BODY, record);
        return;
      }
      const target = trimSlashes(pathname.slice('/subscription/'.length));
      if (method === 'DELETE') {
        const remaining = subscriptions.get(id)?.filter((item) => item !== target);
        if (remaining) subscriptions.set(id, remaining);
        sendJson(res, 200, { Result: 'Success' }, record);
        return;
      }
      if (!endpointBuilders[target]) {
        sendJson(res, 400, { ...NO_SUCH_ENDPOINT_BODY, errorMessage: `${NO_SUCH_ENDPOINT_BODY.errorMessage}: ${target}` }, record);
        return;
      }
      const paths = subscriptions.get(id) ?? [];
      if (!paths.includes(target)) paths.push(target);
      subscriptions.set(id, paths);
      sendJson(res, 200, { Result: 'Success' }, record);
      return;
    }
    sendJson(res, 404, { ...NO_SUCH_ROUTE_BODY, errorMessage: `${NO_SUCH_ROUTE_BODY.errorMessage}: ${pathname}` }, record);
  }

  async function handleRequest(req, res) {
    const requestUrl = new URL(req.url, 'http://127.0.0.1');
    const pathname = requestUrl.pathname;
    const header = req.headers.dtgcommkey;
    const presentedKey = Array.isArray(header) ? header[0] : header;
    const authorized = presentedKey !== undefined && presentedKey === expectedKey;
    const record = {
      method: req.method,
      pathname,
      search: requestUrl.search,
      key: presentedKey ?? null,
      hasValidKey: authorized,
      scenario,
      at: Date.now(),
      status: 0
    };
    // Every request is recorded, including rejected ones: a back-off assertion
    // needs to see attempts, not just successful polls.
    requests.push(record);
    if (requests.length > maxRequests) requests.shift();
    counts[pathname] = countFor(pathname) + 1;

    const profile = SCENARIOS[scenario];
    await delay(delayMs);

    if (profile.rejectEverything || !authorized) {
      sendJson(res, 403, INVALID_KEY_BODY, record);
      return;
    }
    if (profile.envelopeError) {
      sendJson(res, 200, { Result: 'Error', Message: 'dtg.comm.EndpointUnavailable: the game is not ready to answer' }, record);
      return;
    }
    if (req.method === 'OPTIONS') {
      // Answered, but deliberately without Access-Control-Allow-* headers: the
      // docs expect the game to reject browser preflight, so the fake must not
      // make a front-end promise direct access that the real API cannot keep.
      sendJson(res, 200, {}, record);
      return;
    }
    // Subscriptions are the only write routes this client uses (register, read, remove); everything
    // else stays GET-only, which is what the reverse-engineered spec shows.
    const subscriptionRoute = pathname === '/subscription' || pathname === '/subscription/'
      || pathname.startsWith('/subscription/') || pathname === '/listsubscriptions';
    const writeAllowed = subscriptionRoute && (req.method === 'POST' || req.method === 'DELETE');
    if (req.method !== 'GET' && !writeAllowed) {
      sendJson(res, 501, { ...NOT_IMPLEMENTED_BODY, errorMessage: `${NOT_IMPLEMENTED_BODY.errorMessage} (got ${req.method} ${pathname})` }, record);
      return;
    }
    route(pathname, req.method, requestUrl, res, record, profile);
  }

  const server = http.createServer((req, res) => {
    handleRequest(req, res).catch((error) => {
      if (!res.headersSent && !res.writableEnded) {
        sendJson(res, 500, { errorCode: 'dtg.comm.InternalError', errorMessage: String(error?.message ?? error) }, null);
      } else if (!res.destroyed) {
        res.destroy();
      }
    });
  });
  server.on('connection', (socket) => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
  });
  server.on('clientError', (error, socket) => {
    if (socket.writable) socket.end('HTTP/1.1 400 Bad Request\r\n\r\n');
  });
  server.on('error', (error) => {
    lastError = error;
  });

  async function bind(candidatePort, allowFallback) {
    try {
      await new Promise((resolve, reject) => {
        const onError = (error) => {
          server.removeListener('listening', onListening);
          reject(error);
        };
        const onListening = () => {
          server.removeListener('error', onError);
          resolve();
        };
        server.once('error', onError);
        server.once('listening', onListening);
        server.listen(candidatePort, host);
      });
      port = server.address().port;
    } catch (error) {
      if (error.code === 'EADDRINUSE' && allowFallback && candidatePort !== 0) {
        await bind(0, false);
        return;
      }
      throw error;
    }
  }

  async function stop() {
    // stop() and close() are the same function, and tests call both, so it has to
    // be idempotent rather than re-closing an already closed server.
    if (stopped) return;
    stopped = true;
    scenario = 'offline';
    for (const [timer, resolve] of timers) {
      clearTimeout(timer);
      resolve();
    }
    timers.clear();
    // Sockets are destroyed explicitly because a client's keep-alive pool would
    // otherwise hold the port open after close(), and the test suite asserts
    // that the port is free again.
    for (const socket of sockets) socket.destroy();
    sockets.clear();
    if (typeof server.closeAllConnections === 'function') server.closeAllConnections();
    await new Promise((resolve) => {
      server.close(() => resolve());
    });
  }

  async function setScenario(name, overrides = {}) {
    const profile = SCENARIOS[name];
    if (!profile) {
      throw new Error(`unknown TSW fake scenario '${name}'; expected one of ${SCENARIO_NAMES.join(', ')}`);
    }
    if (stopped && !profile.closed) {
      throw new Error('the fake TSW API is stopped (offline); create a new fake with createFakeTswApi() to serve again');
    }
    // The switch is applied synchronously before the first await, so callers who
    // forget to await a non-offline scenario still get the new behaviour; only
    // 'offline' genuinely needs the await, because it closes the listener.
    scenario = name;
    delayMs = overrides.delayMs ?? profile.delayMs ?? 0;
    if (profile.closed) await stop();
    return name;
  }

  function setState(patch = {}) {
    const accepted = ['latitude', 'longitude', 'metresPerSecond', 'currentTile', 'currentServiceName', 'playerProfileName'];
    for (const key of Object.keys(patch)) {
      // Throwing on a typo beats a silent no-op that turns into a green test
      // asserting a value the fake never served.
      if (!accepted.includes(key)) {
        throw new Error(`setState() does not accept '${key}'; accepted keys: ${accepted.join(', ')}`);
      }
    }
    if (patch.latitude !== undefined) state.latitude = patch.latitude;
    if (patch.longitude !== undefined) state.longitude = patch.longitude;
    if (patch.metresPerSecond !== undefined) state.metresPerSecond = patch.metresPerSecond;
    if (patch.currentTile !== undefined) state.currentTile = { ...patch.currentTile };
    if (patch.currentServiceName !== undefined) state.currentServiceName = patch.currentServiceName;
    if (patch.playerProfileName !== undefined) state.playerProfileName = patch.playerProfileName;
    return fake;
  }

  function setDriverAid(patch) {
    if (patch === null || typeof patch !== 'object' || Array.isArray(patch)) {
      throw new Error('setDriverAid() needs a plain object patch, for example { speedLimit: { value: 8.33 } }');
    }
    mergeValues(state.driverAid, patch);
    return fake;
  }

  function resetRequests() {
    requests.length = 0;
    for (const key of Object.keys(counts)) delete counts[key];
    return fake;
  }

  function resetState() {
    Object.assign(state, defaultState());
    return fake;
  }

  async function waitForCount(pathname, expected, waitOptions = {}) {
    const timeoutMs = waitOptions.timeoutMs ?? 2000;
    const deadline = Date.now() + timeoutMs;
    while (countFor(pathname) < expected) {
      if (Date.now() > deadline) {
        throw new Error(`timed out after ${timeoutMs} ms waiting for ${expected} request(s) to ${pathname}; saw ${countFor(pathname)}`);
      }
      await delay(10);
    }
    return countFor(pathname);
  }

  const fake = {
    // baseUrl/port keep reporting the last bound address even after stop(), so a
    // test can assert that a client now fails with ECONNREFUSED there.
    get baseUrl() { return `http://${host}:${port}`; },
    get port() { return port; },
    get host() { return host; },
    get key() { return expectedKey; },
    get scenario() { return scenario; },
    get stopped() { return stopped; },
    get lastError() { return lastError; },
    get server() { return server; },
    requests,
    counts,
    countFor,
    setScenario,
    setState,
    setDriverAid,
    resetRequests,
    resetState,
    waitForCount,
    url(pathname = '/') { return `http://${host}:${port}${pathname}`; },
    stop,
    close: stop
  };

  await bind(requestedPort, fallbackToEphemeral);
  await setScenario(options.scenario ?? 'ok');
  return fake;
}

// Manual mode: `node test/tsw-fake-api.js [port]` serves until Ctrl+C so a real
// client can be pointed at the fake. Importing this module does nothing.
const isMainModule = (() => {
  const entry = process.argv[1];
  if (!entry) return false;
  const normalize = (value) => (process.platform === 'win32' ? path.resolve(value).toLowerCase() : path.resolve(value));
  return normalize(entry) === normalize(fileURLToPath(import.meta.url));
})();

// NODE_TEST_CONTEXT is what makes `node --test` safe here. Because this file
// lives in test/, the runner discovers it as a test file and spawns it as a
// child entry point, so argv[1] does match it -- without this guard the runner
// would block forever on a server that never exits. The runner sets that
// variable for every child it spawns; a human running the file never has it.
const isManualRun = isMainModule && process.env.NODE_TEST_CONTEXT === undefined;

if (isManualRun) {
  const requested = Number.parseInt(process.argv[2] ?? '', 10);
  const port = Number.isInteger(requested) ? requested : DEFAULT_PORT;
  const scenario = process.argv[3] ?? 'ok';
  const fake = await createFakeTswApi({ port, fallbackToEphemeral: true, scenario });
  const key = fake.key;
  console.log(`TSW6 fake API listening on ${fake.baseUrl}`);
  if (fake.port !== port) console.log(`(port ${port} was busy, so an ephemeral port was used instead)`);
  console.log(`expected request header: DTGCommKey: ${key}`);
  console.log(`scenario: ${fake.scenario} (fixed while running this way; import the module to switch)`);
  console.log('try:');
  console.log(`  curl -s -H "DTGCommKey: ${key}" ${fake.baseUrl}/info`);
  console.log(`  curl -s -H "DTGCommKey: ${key}" ${fake.baseUrl}/get/DriverAid.PlayerInfo`);
  console.log(`  curl -s -H "DTGCommKey: ${key}" "${fake.baseUrl}/get/CurrentDrivableActor.Function.HUD_GetSpeed"`);
  console.log(`  curl -s -X POST -H "DTGCommKey: ${key}" "${fake.baseUrl}/subscription/DriverAid.PlayerInfo?Subscription=1"`);
  console.log(`  curl -s -H "DTGCommKey: ${key}" "${fake.baseUrl}/subscription?Subscription=1"`);
  console.log(`point a client at EFB_TSW_BASE=${fake.baseUrl} and EFB_TSW_KEY=${key}`);
  console.log('Ctrl+C to stop.');
  const shutdown = async () => {
    await fake.stop();
    process.exit(0);
  };
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
}
