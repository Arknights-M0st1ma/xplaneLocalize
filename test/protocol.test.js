import test from 'node:test';
import assert from 'node:assert/strict';
import {
  MS_TO_KMH, SOURCE_TSW, SOURCE_XPLANE, TSW_PROTOCOL,
  formatMetres, instrumentLabels, panelVisibility, primarySpeed, secondaryReadouts, sourceInfo, statusText, trainPhase
} from '../public/protocol.js';

test('a TSW frame selects train mode whatever the configuration says', () => {
  assert.equal(sourceInfo(TSW_PROTOCOL, SOURCE_XPLANE).isTrain, true);
  assert.equal(sourceInfo('DATA', SOURCE_TSW).isTrain, false);
  // No frame yet: the configured source decides, so the UI is right before the first frame.
  assert.equal(sourceInfo(undefined, SOURCE_TSW).isTrain, true);
  assert.equal(sourceInfo(undefined, SOURCE_XPLANE).isTrain, false);
  assert.equal(sourceInfo(null, undefined).isTrain, false, 'missing config must not throw');
});

test('train instruments drop the heading and use km/h', () => {
  const train = instrumentLabels(true);
  assert.equal(train.speed, 'KM/H');
  assert.equal(train.heading, '', 'the game API has no heading field, so the instrument is hidden');
  assert.equal(instrumentLabels(false).speed, 'KT');
  assert.equal(instrumentLabels(false).heading, 'TRUE');
});

test('primary speed reads the right field for each source', () => {
  assert.equal(primarySpeed({ groundSpeedKmh: 84.3 }, true), 84.3);
  assert.equal(primarySpeed({ groundSpeedKt: 148 }, false), 148);
  // A train frame that only carries knots is converted, so a partial frame still shows a speed.
  assert.ok(Math.abs(primarySpeed({ groundSpeedKt: 45.5 }, true) - 84.3) < 0.1);
  assert.equal(primarySpeed({ indicatedAirspeedKt: 125 }, false), 125, 'falls back to IAS');
  assert.equal(primarySpeed({}, true), null);
  assert.equal(primarySpeed(null, false), null);
});

test('train secondary readouts use the speed limit and its distance', () => {
  const train = secondaryReadouts({ limitKmh: 60, distanceToNextLimitM: 1250 }, true);
  assert.equal(train.altitude, 60);
  assert.equal(train.vertical, 1250);
  // Missing driver-aid data must degrade to null so the caller prints an em dash.
  const bare = secondaryReadouts({ latitude: 51.5 }, true);
  assert.equal(bare.altitude, null);
  assert.equal(bare.vertical, null);
  const aircraft = secondaryReadouts({ altitudeMslFt: 12000, verticalSpeedFpm: -800 }, false);
  assert.equal(aircraft.altitude, 12000);
  assert.equal(aircraft.vertical, -800);
});

test('aviation-only panels are hidden for the train and weather is kept', () => {
  const train = panelVisibility(true);
  assert.equal(train.simbrief, false, 'SimBrief flight plans mean nothing to a train');
  assert.equal(train.ground, false, 'apt.dat holds airport taxiways, not railways');
  assert.equal(train.airline, false, 'airline badges come from the callsign');
  assert.equal(train.headingInstrument, false);
  assert.equal(train.weather, true, 'real weather matters to a driver too');
  assert.deepEqual(panelVisibility(false), {
    simbrief: true, ground: true, airline: true, headingInstrument: true, weather: true
  });
});

test('status text never says UDP while the train source is active', () => {
  assert.equal(statusText('live', true), '列车数据实时');
  assert.equal(statusText('lost', true), 'TSW 数据超时');
  assert.equal(statusText('waiting', true), '等待 TSW 数据');
  assert.equal(statusText('live', false), '数据实时');
  assert.equal(statusText('unknown-kind', false), '');
});

test('distances switch to kilometres where that reads better', () => {
  assert.equal(formatMetres(0), '0 m');
  assert.equal(formatMetres(999), '999 m');
  assert.equal(formatMetres(1250), '1.3 km');
  assert.equal(formatMetres(54321), '54 km');
  assert.equal(formatMetres(undefined), '—', 'no driver aid means no guessing');
});

test('train phase is a simple ground/rolling split', () => {
  assert.equal(trainPhase(0), '停车');
  assert.equal(trainPhase(3), '调车');
  assert.equal(trainPhase(84.3), '运行');
  assert.equal(trainPhase(undefined), '—');
  assert.ok(Math.abs(0.5 * MS_TO_KMH) > 0, 'the constant is exported for callers that need raw m/s');
});
