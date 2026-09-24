import test from 'node:test';
import assert from 'node:assert/strict';
import { createFakeTswApi, INVALID_KEY_BODY, NO_SUCH_SUBSCRIPTION_BODY, TSW_SENTINEL } from './tsw-fake-api.js';

const PLAYER_INFO = '/get/DriverAid.PlayerInfo';
const HUD_SPEED = '/get/CurrentDrivableActor.Function.HUD_GetSpeed';
const DRIVER_AID = '/get/DriverAid.Data';

// Every request carries the fake's key unless a test overrides it; key: null
// means "send no DTGCommKey header at all", which is the unconfigured case.
function request(fake, pathname, options = {}) {
  const headers = {};
  const key = Object.hasOwn(options, 'key') ? options.key : fake.key;
  if (key !== null && key !== undefined) headers.DTGCommKey = key;
  return fetch(`${fake.baseUrl}${pathname}`, { headers, signal: options.signal });
}

async function getJson(fake, pathname, options = {}) {
  const response = await request(fake, pathname, options);
  return { status: response.status, body: await response.json() };
}

test('answers 403 with dtg.comm.InvalidKey when the key is missing or wrong', async (context) => {
  // Why: a 403 is the client's signal that CommAPIKey.txt is missing or stale, so
  // it must back off and never treat the error body as telemetry.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());

  for (const key of [null, 'definitely-not-the-key']) {
    const { status, body } = await getJson(fake, '/list', { key });
    assert.equal(status, 403);
    assert.equal(body.errorCode, 'dtg.comm.InvalidKey');
    assert.equal(body.errorMessage, INVALID_KEY_BODY.errorMessage);
    assert.match(body.errorMessage, /CommAPIKey\.txt/);
    assert.equal(body.Result, undefined);
    assert.equal(body.Values, undefined);
  }

  // The key can also go stale between polls: the game rejects the right key too.
  await fake.setScenario('bad-key');
  const stale = await getJson(fake, '/list');
  assert.equal(stale.status, 403);
  assert.equal(stale.body.errorCode, 'dtg.comm.InvalidKey');
});

test('serves Result Success for /info, /list and every telemetry endpoint', async (context) => {
  // Why: this is the healthy baseline the C# client is written against; if the
  // envelope shape here drifts, every parsing test built on it drifts too.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());

  const info = await getJson(fake, '/info');
  assert.equal(info.status, 200);
  assert.equal(info.body.Meta.Worker, 'DTGCommWorkerRC');
  assert.match(info.body.Meta.GameName, /Train Sim World 6/);
  assert.ok(Array.isArray(info.body.HttpRoutes));

  const list = await getJson(fake, '/list');
  assert.equal(list.status, 200);
  assert.equal(list.body.Result, 'Success');
  assert.ok(list.body.Nodes.some((node) => node.Name === 'DriverAid'));

  for (const pathname of [PLAYER_INFO, HUD_SPEED, DRIVER_AID]) {
    const { status, body } = await getJson(fake, pathname);
    assert.equal(status, 200);
    assert.equal(body.Result, 'Success');
    assert.equal(typeof body.Values, 'object');
  }
});

test('exposes the speed field under the literal key "Speed (ms)"', async (context) => {
  // Why: the key contains a space and parentheses, so a client that builds JSON
  // paths by hand, or a UI that uses the name as a selector, silently reads
  // nothing and reports a 0 km/h train.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());
  fake.setState({ metresPerSecond: 23.42 });

  const { body } = await getJson(fake, HUD_SPEED);
  assert.deepEqual(Object.keys(body.Values), ['Speed (ms)']);
  assert.equal(body.Values['Speed (ms)'], 23.42);
  // The client must convert m/s before display: 23.42 m/s is 84.312 km/h.
  assert.ok(Math.abs(body.Values['Speed (ms)'] * 3.6 - 84.312) < 0.001);
});

test('setState and setDriverAid change what the telemetry endpoints serve', async (context) => {
  // Why: tests of polling, de-duplication and map movement need a train that
  // actually moves, not a frozen fixture.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());

  fake.setState({ latitude: 52.5163, longitude: 13.3777, metresPerSecond: 41.7 });
  const player = (await getJson(fake, PLAYER_INFO)).body.Values;
  assert.equal(player.geoLocation.latitude, 52.5163);
  assert.equal(player.geoLocation.longitude, 13.3777);
  assert.equal((await getJson(fake, HUD_SPEED)).body.Values['Speed (ms)'], 41.7);

  fake.setDriverAid({ speedLimit: { value: 8.33 }, distanceToSignal: 120 });
  const aid = (await getJson(fake, DRIVER_AID)).body.Values;
  assert.equal(aid.speedLimit.value, 8.33);
  assert.equal(aid.distanceToSignal, 120);
  // It is a deep merge, so untouched siblings must survive.
  assert.equal(aid.nextSpeedLimit.value, 13.41);
  assert.equal(aid.signalAspectClass, 'Clear');

  // A typo must fail loudly instead of silently serving the old value.
  assert.throws(() => fake.setState({ speed: 10 }), /does not accept 'speed'/);
});

test('lists nodes and endpoints, including a node path that contains a slash', async (context) => {
  // Why: /list is how the client discovers writable endpoints and how a developer
  // checks a field name against the real game; the '/' node path form is called
  // out explicitly in the brief.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());

  const driverAid = await getJson(fake, '/list/DriverAid');
  assert.equal(driverAid.body.Result, 'Success');
  assert.deepEqual(driverAid.body.Nodes.map((node) => node.Name), ['PlayerInfo', 'Data']);
  assert.equal(driverAid.body.Endpoints.every((endpoint) => endpoint.Writable === false), true);

  const wiper = await getJson(fake, '/list/DriverInput/Wiper_F');
  assert.equal(wiper.body.Result, 'Success');
  assert.equal(wiper.body.NodeName, 'Wiper_F');
  assert.deepEqual(wiper.body.Endpoints, [{ Name: 'Value', Writable: true }]);
});

test('answers 400 for an unknown subscription id and for an unknown endpoint', async (context) => {
  // Why: the bridge falls back to /subscription when no /get candidate carries a
  // position, so "this id was never registered" has to stay distinguishable from
  // "this endpoint does not exist".
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());

  const subscription = await getJson(fake, '/subscription?Subscription=7');
  assert.equal(subscription.status, 400);
  assert.equal(subscription.body.errorCode, 'dtg.comm.NoSuchSubscription');
  assert.equal(subscription.body.errorMessage, NO_SUCH_SUBSCRIPTION_BODY.errorMessage);

  const endpoint = await getJson(fake, '/get/DriverAid.NotARealEndpoint');
  assert.equal(endpoint.status, 400);
  assert.match(endpoint.body.errorMessage, /DriverAid\.NotARealEndpoint/);
});

test('serves a working subscription, and subscription-only hides the position from /get', async (context) => {
  // Why: both public TSW6 clients read the player position from /subscription rather than /get, so
  // the bridge falls back to that shape. This scenario is the one that looks healthy through /get
  // and still leaves the map empty - the failure the field-name guesses could not explain.
  const fake = await createFakeTswApi({ scenario: 'subscription-only' });
  context.after(() => fake.stop());

  const viaGet = await getJson(fake, PLAYER_INFO);
  assert.equal(viaGet.status, 200);
  assert.equal(viaGet.body.Result, 'Success');
  assert.equal(viaGet.body.Values.geoLocation, undefined, 'the position is deliberately absent here');
  assert.ok(viaGet.body.Values.currentServiceName.length > 0);

  const register = (id) => fetch(`${fake.baseUrl}/subscription/DriverAid.PlayerInfo?Subscription=${id}`, {
    method: 'POST',
    headers: { DTGCommKey: fake.key }
  });
  const remove = (id) => fetch(`${fake.baseUrl}/subscription/?Subscription=${id}`, {
    method: 'DELETE',
    headers: { DTGCommKey: fake.key }
  });

  const registered = await register(42);
  assert.equal(registered.status, 200);
  assert.equal((await registered.json()).Result, 'Success');

  const read = await getJson(fake, '/subscription?Subscription=42');
  assert.equal(read.status, 200);
  assert.equal(read.body.RequestedSubscriptionID, 42);
  assert.equal(read.body.Entries.length, 1);
  const values = read.body.Entries[0].Values;
  assert.ok(Number.isFinite(values.geoLocation.latitude));
  assert.ok(Number.isFinite(values.geoLocation.longitude));
  assert.equal(values.currentServiceName, viaGet.body.Values.currentServiceName);

  // Removing it puts the id back to "unknown", which is what stops a stopped bridge from leaving a
  // subscription behind on the game side.
  assert.equal((await remove(42)).status, 200);
  const after = await getJson(fake, '/subscription?Subscription=42');
  assert.equal(after.status, 400);
  assert.equal(after.body.errorCode, 'dtg.comm.NoSuchSubscription');

  // Registering a path the game does not expose must not silently succeed.
  const unknownPath = await fetch(`${fake.baseUrl}/subscription/DriverAid.NotARealEndpoint?Subscription=43`, {
    method: 'POST',
    headers: { DTGCommKey: fake.key }
  });
  assert.equal(unknownPath.status, 400);
  assert.match((await unknownPath.json()).errorMessage, /NotARealEndpoint/);
});

test('the error-envelope scenario is HTTP 200 carrying Result "Error"', async (context) => {
  // Why: this is the case that punishes status-code-only clients. Our C# client
  // must branch on Result and must not read Values out of an error envelope.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());
  await fake.setScenario('error-envelope');

  for (const pathname of ['/info', '/list', PLAYER_INFO, HUD_SPEED, DRIVER_AID]) {
    const { status, body } = await getJson(fake, pathname);
    assert.equal(status, 200);
    assert.equal(body.Result, 'Error');
    assert.equal(typeof body.Message, 'string');
    assert.equal(body.Values, undefined);
  }

  // Authentication still runs first, so a stale key stays a 403 here.
  const unauthorized = await getJson(fake, '/list', { key: 'wrong' });
  assert.equal(unauthorized.status, 403);
});

test('the no-info scenario 404s /info while the other routes keep working', async (context) => {
  // Why: /info is absent from the reverse-engineered spec, so the client needs a
  // fallback liveness probe; this scenario is what that fallback must survive.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());
  await fake.setScenario('no-info');

  const info = await getJson(fake, '/info');
  assert.equal(info.status, 404);
  assert.equal(info.body.errorCode, 'dtg.comm.NoSuchRoute');

  assert.equal((await getJson(fake, '/list')).body.Result, 'Success');
  assert.equal((await getJson(fake, PLAYER_INFO)).body.Result, 'Success');
});

test('the minimal scenario serves renamed fields, missing fields and sentinels', async (context) => {
  // Why: field names come from third-party docs, so the parser must tolerate
  // renames, absences and the 3.4028e+38 "undefined" marker without ever showing
  // 3.4e38 metres or a null island position.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());
  await fake.setScenario('minimal');
  fake.setState({ latitude: 48.8566, longitude: 2.3522, metresPerSecond: 12.5 });

  assert.equal(TSW_SENTINEL, 3.4028e38);

  const player = (await getJson(fake, PLAYER_INFO)).body.Values;
  assert.equal('geoLocation' in player, false);
  assert.equal(player.lat, 48.8566);
  assert.equal(player.currentTile.x, TSW_SENTINEL);

  const speed = (await getJson(fake, HUD_SPEED)).body.Values;
  assert.equal('Speed (ms)' in speed, false);
  assert.equal(speed.Speed, 12.5);

  const aid = (await getJson(fake, DRIVER_AID)).body.Values;
  assert.equal(aid.speedLimit.value, TSW_SENTINEL);
  assert.equal(aid.distanceToNextSpeedLimit, TSW_SENTINEL);
  assert.equal('distanceToSignal' in aid, false);
  assert.equal(aid.signalAspectClass, '');
});

test('records every request with its path, key validity and status', async (context) => {
  // Why: polling frequency and back-off are behaviour, not implementation, and the
  // only way to assert them is to count what actually arrived.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());

  for (let attempt = 0; attempt < 3; attempt += 1) {
    await getJson(fake, PLAYER_INFO);
  }
  // A rejected request is still recorded: back-off needs to see attempts.
  await getJson(fake, '/list', { key: null });

  assert.equal(fake.countFor(PLAYER_INFO), 3);
  assert.equal(fake.countFor('/list'), 1);
  assert.equal(fake.requests.length, 4);

  const rejected = fake.requests.at(-1);
  assert.equal(rejected.method, 'GET');
  assert.equal(rejected.pathname, '/list');
  assert.equal(rejected.hasValidKey, false);
  assert.equal(rejected.key, null);
  assert.equal(rejected.status, 403);
  assert.equal(rejected.scenario, 'ok');
  assert.equal(typeof rejected.at, 'number');

  // Back-off: once the client stops polling, the count must stay put.
  const polled = fake.countFor(PLAYER_INFO);
  await new Promise((resolve) => setTimeout(resolve, 40));
  assert.equal(fake.countFor(PLAYER_INFO), polled);

  // A request a test did not await is still visible to waitForCount().
  const inFlight = getJson(fake, PLAYER_INFO);
  assert.equal(await fake.waitForCount(PLAYER_INFO, 4), 4);
  await inFlight;

  fake.resetRequests();
  assert.equal(fake.requests.length, 0);
  assert.equal(fake.countFor(PLAYER_INFO), 0);
});

test('the slow scenario lets a client timeout fire yet still serves later requests', async (context) => {
  // Why: the client needs a timeout so a hung game cannot stall the bridge, and
  // the fake has to be slow rather than broken so both halves can be tested.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());
  await fake.setScenario('slow', { delayMs: 250 });

  await assert.rejects(
    () => request(fake, HUD_SPEED, { signal: AbortSignal.timeout(50) }),
    (error) => error?.name === 'TimeoutError' || error?.name === 'AbortError'
  );
  // The abandoned attempt is still recorded, so "slow" is distinguishable
  // from "never polled".
  assert.equal(fake.countFor(HUD_SPEED), 1);

  const patient = await getJson(fake, HUD_SPEED);
  assert.equal(patient.body.Result, 'Success');
  assert.equal(fake.countFor(HUD_SPEED), 2);
});

test('the offline scenario refuses connections instead of answering', async (context) => {
  // Why: this is what the client sees when the game is closed or -HTTPAPI was not
  // passed, and it must be distinguishable from a 403 or a malformed body.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());
  await fake.setScenario('offline');

  await assert.rejects(
    () => request(fake, '/info'),
    (error) => {
      const code = error?.cause?.code ?? error?.code;
      return code === 'ECONNREFUSED' || code === 'ECONNRESET' || /ECONNREFUSED/.test(String(error?.cause?.message ?? error?.message));
    }
  );

  // The bound port is gone, so coming back online needs a fresh instance.
  await assert.rejects(() => fake.setScenario('ok'), /create a new fake/);
});

test('stop() and close() really release the port', async (context) => {
  // Why: a leaked listener makes a later test in the same process fail with
  // EADDRINUSE, which looks like a client bug but is a fake bug.
  const fake = await createFakeTswApi();
  const port = fake.port;
  assert.equal((await getJson(fake, '/list')).status, 200);

  await fake.stop();
  assert.equal(fake.stopped, true);
  assert.equal(fake.scenario, 'offline');
  await fake.stop();

  // Re-binding the same port is the proof; an ephemeral collision would hide it.
  const rebound = await createFakeTswApi({ port });
  context.after(() => rebound.stop());
  assert.equal(rebound.port, port);
  assert.equal((await getJson(rebound, '/list')).status, 200);

  await fake.close();
  assert.equal(fake.stopped, true);
});

test('an unknown scenario name is rejected and leaves the fake on its scenario', async (context) => {
  // Why: a typo in a test must fail the test, not silently keep serving the
  // healthy scenario while the test asserts hostile behaviour.
  const fake = await createFakeTswApi();
  context.after(() => fake.stop());

  await assert.rejects(() => fake.setScenario('nope'), /unknown TSW fake scenario/);
  assert.equal(fake.scenario, 'ok');
  assert.equal((await getJson(fake, '/list')).body.Result, 'Success');
});
