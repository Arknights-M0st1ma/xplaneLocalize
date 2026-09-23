import test from 'node:test';
import assert from 'node:assert/strict';
import { TSW_DEMO_PROTOCOL, createTswDemo } from '../src/tswDemo.js';

// The demo exists so the train front end can be built without the game (which only runs on the
// machine that has TSW6 installed). These checks keep it honest: it must emit the field names the
// real bridge emits, and it must not drift into impossible values that would hide a UI bug.

test('frames carry the field names the bridge publishes for the train source', () => {
  const demo = createTswDemo();
  const frame = demo.frame(250);
  assert.equal(frame.protocol, TSW_DEMO_PROTOCOL);
  for (const key of ['latitude', 'longitude', 'groundSpeedKmh', 'groundSpeedKt', 'limitKmh',
    'nextLimitKmh', 'distanceToNextLimitM', 'distanceToSignalM', 'signalAspect', 'gradient',
    'currentServiceName', 'receivedAt']) {
    assert.ok(key in frame, `missing demo field: ${key}`);
  }
  // The game API has no heading, so a heading in the demo would make the front end look correct
  // while the real source shows nothing.
  assert.equal('headingTrueDeg' in frame, false);
  assert.equal('altitudeMslFt' in frame, false);
});

test('the train moves along the route, slows for lower limits and comes to a stand', () => {
  // The line is ~48 km and the simulated clock runs at 8x, so one complete run fits in a few hundred
  // frames; 2500 of them covers more than a full cycle including the station stop.
  const demo = createTswDemo({ speedFactor: 8 });
  const first = demo.frame(250);
  assert.ok(Math.abs(first.latitude - 51.5310) < 0.01, 'starts at the beginning of the route');

  const speeds = [];
  const longitudes = [];
  for (let index = 0; index < 2500; index += 1) {
    const frame = demo.frame(250);
    speeds.push(frame.groundSpeedKmh);
    longitudes.push(frame.longitude);
  }
  assert.ok(Math.min(...longitudes) < first.longitude - 0.1, 'the train travels a real distance west');
  assert.ok(Math.max(...speeds) > 100, `expected a cruise above 100 km/h, got ${Math.max(...speeds)}`);
  assert.ok(Math.min(...speeds) >= 0, 'speed never goes negative');
  assert.ok(speeds.filter((value) => value === 0).length > 0, 'it stops at the end of the line');
});

// Regression: the first version chased the limit in force and only braked once the lower limit was
// already close, so it reported absurd speeds (643 km/h inside a 40 zone) at large time steps. That
// would have masked real front-end bugs behind nonsense, so the braking-distance model is asserted
// across the whole range of time steps the caller can produce, including a stalled event loop.
test('the train never overshoots the limit in force, at any time step', () => {
  for (const speedFactor of [1, 4, 20, 200]) {
    const demo = createTswDemo({ speedFactor });
    let worst = 0;
    let highest = 0;
    for (let index = 0; index < 4000; index += 1) {
      const frame = demo.frame(250);
      worst = Math.max(worst, frame.groundSpeedKmh - frame.limitKmh);
      highest = Math.max(highest, frame.groundSpeedKmh);
    }
    assert.ok(worst <= 1, `speedFactor ${speedFactor}: overshot the limit by ${worst.toFixed(2)} km/h`);
    assert.ok(highest <= 125, `speedFactor ${speedFactor}: reached ${highest.toFixed(1)} km/h`);
  }

  const stalled = createTswDemo();
  let worstStalled = 0;
  for (let index = 0; index < 1000; index += 1) {
    const frame = stalled.frame(30000);
    worstStalled = Math.max(worstStalled, frame.groundSpeedKmh - frame.limitKmh);
  }
  assert.ok(worstStalled <= 1, `a 30 s frame gap overshot by ${worstStalled.toFixed(2)} km/h`);
});

test('speed limits are declared and the next limit is reported ahead of the train', () => {
  const demo = createTswDemo();
  for (let index = 0; index < 200; index += 1) {
    const frame = demo.frame(250);
    assert.ok([0, 40, 60, 80, 100, 125].includes(frame.limitKmh), `unexpected limit ${frame.limitKmh}`);
    assert.ok(frame.distanceToNextLimitM >= 0);
  }
});

test('the signal aspect follows the distance to the signal', () => {
  const demo = createTswDemo();
  const aspects = new Set();
  for (let index = 0; index < 600; index += 1) aspects.add(demo.frame(250).signalAspect);
  assert.ok(aspects.has('Clear'), 'a green aspect must appear');
  assert.ok([...aspects].every((value) => ['Clear', 'Caution', 'Danger'].includes(value)));
});

test('the run restarts at the end of the line instead of freezing', () => {
  const demo = createTswDemo({ speedFactor: 8 });
  let wraps = 0;
  let previous = demo.frame(250).longitude;
  for (let index = 0; index < 2500; index += 1) {
    const frame = demo.frame(250);
    // A restart is the position jumping back to the start of the line, i.e. the longitude growing
    // again after it had been heading west.
    if (frame.longitude > previous + 0.05) wraps += 1;
    previous = frame.longitude;
  }
  assert.ok(wraps > 0, 'the simulation loops back to a fresh departure');
  assert.ok(demo.travelledMetres <= demo.totalMetres, 'it never runs past the end of the route');
});
