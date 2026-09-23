import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createTrackStore } from '../src/track.js';

const at = (store, lat, lng, seconds) => store.record([lat, lng], seconds * 1000);

test('records the first position and then throttles', () => {
  const store = createTrackStore();
  assert.equal(at(store, 31.14, 121.8, 0), true);
  // Same place, too soon: nothing new.
  assert.equal(at(store, 31.14, 121.8, 0.4), false);
  assert.equal(store.size, 1);
});

test('a real movement is recorded even inside the heartbeat', () => {
  const store = createTrackStore();
  at(store, 31.14, 121.8, 0);
  // ~0.5 NM north after 1 s: worth a point.
  assert.equal(at(store, 31.1483, 121.8, 1), true);
  assert.equal(store.size, 2);
});

test('a parked aircraft still gets a heartbeat point', () => {
  const store = createTrackStore();
  at(store, 31.14, 121.8, 0);
  assert.equal(at(store, 31.14, 121.8, 3), false);
  assert.equal(at(store, 31.14, 121.8, 5), true);
  assert.equal(store.size, 2);
});

test('invalid positions never enter the track', () => {
  const store = createTrackStore();
  assert.equal(store.record([0, 0], 0), false);
  assert.equal(store.record([NaN, 10], 1), false);
  assert.equal(store.record([91, 10], 2), false);
  assert.equal(store.record([10, 181], 3), false);
  assert.equal(store.record(null, 4), false);
  assert.equal(store.size, 0);
});

test('the buffer is capped and drops the oldest points', () => {
  const store = createTrackStore({ maxPoints: 5 });
  for (let i = 0; i < 12; i += 1) at(store, 31 + i * 0.01, 121, i * 5);
  assert.equal(store.size, 5);
  const { points, first } = store.read();
  assert.equal(points.length, 5);
  assert.equal(first, 35 * 1000);
});

test('an incremental read returns only what the client is missing', () => {
  const store = createTrackStore();
  at(store, 31.14, 121.8, 0);
  at(store, 31.20, 121.8, 10);
  at(store, 31.26, 121.8, 20);
  const first = store.read();
  assert.equal(first.points.length, 3);
  assert.equal(first.replaced, true);
  const delta = store.read({ since: 10000 });
  assert.equal(delta.replaced, false);
  assert.deepEqual(delta.points.map((point) => point[2]), [20000]);
});

test('a read from before the retained window asks the client to replace', () => {
  const store = createTrackStore();
  at(store, 31.14, 121.8, 0);
  at(store, 31.20, 121.8, 10);
  // After a clear the client's cursor points at points the bridge no longer has.
  store.clear();
  at(store, 31.30, 121.8, 30);
  const result = store.read({ since: 10000 });
  assert.equal(result.replaced, true);
  assert.equal(result.points.length, 1);
});

test('clear empties the track', () => {
  const store = createTrackStore();
  at(store, 31.14, 121.8, 0);
  assert.equal(store.clear(), 1);
  assert.equal(store.size, 0);
  assert.equal(store.read().points.length, 0);
});

test('the track survives a bridge restart', () => {
  const file = path.join(os.tmpdir(), `efb-track-test-${process.pid}-${Math.random().toString(16).slice(2)}.json`);
  try {
    const first = createTrackStore({ savePath: file });
    at(first, 31.14, 121.8, 0);
    at(first, 31.2, 121.8, 10);
    assert.equal(first.save(true), true);
    const second = createTrackStore({ savePath: file });
    assert.equal(second.load(), 2);
    assert.equal(second.read().points.length, 2);
    assert.equal(second.read().points[0][0], 31.14);
  } finally {
    fs.rmSync(file, { force: true });
  }
});

test('a corrupt save file is ignored instead of throwing', () => {
  const file = path.join(os.tmpdir(), `efb-track-bad-${process.pid}-${Math.random().toString(16).slice(2)}.json`);
  fs.writeFileSync(file, '{not json', 'utf8');
  try {
    const store = createTrackStore({ savePath: file });
    assert.equal(store.load(), 0);
    assert.equal(store.size, 0);
  } finally {
    fs.rmSync(file, { force: true });
  }
});
