import assert from 'node:assert/strict';
import test from 'node:test';
import { WEATHER_MAX_ZOOM, createTileCache, upstreamName, upstreamUrl, validCoordinate, weatherLayers } from '../src/weather.js';

test('only the five allow-listed layers are accepted', () => {
  assert.equal(upstreamName('clouds'), 'clouds_new');
  assert.equal(upstreamName('precipitation'), 'precipitation_new');
  assert.equal(upstreamName('../secret'), null);
  assert.equal(upstreamName('tem'), null);
  assert.equal(weatherLayers().length, 5);
});

test('tile coordinates are range checked', () => {
  assert.equal(validCoordinate('clouds', 0, 0, 0), true);
  assert.equal(validCoordinate('clouds', 3, 7, 7), true);
  assert.equal(validCoordinate('clouds', 3, 8, 0), false);
  assert.equal(validCoordinate('clouds', 3, -1, 0), false);
  assert.equal(validCoordinate('clouds', WEATHER_MAX_ZOOM + 1, 0, 0), false);
  assert.equal(validCoordinate('nope', 1, 0, 0), false);
});

test('upstream url carries the key and the mapped layer name', () => {
  assert.equal(
    upstreamUrl('wind', 'abc 123', 4, 8, 5),
    'https://tile.openweathermap.org/map/wind_new/4/8/5.png?appid=abc%20123'
  );
});

test('tile cache expires and the request budget is enforced', () => {
  let clock = 0;
  const cache = createTileCache({ ttlMs: 1000, maxPerMinute: 2, now: () => clock });
  cache.set('clouds/1/0/0', Buffer.from('x'), 'image/png');
  assert.ok(cache.get('clouds/1/0/0'));
  clock = 1500;
  assert.equal(cache.get('clouds/1/0/0'), null);
  assert.equal(cache.allow(), true);
  assert.equal(cache.allow(), true);
  assert.equal(cache.allow(), false);
  clock = 61000;
  assert.equal(cache.allow(), true);
});

test('counts empty tiles per layer so the UI can explain an invisible layer', () => {
  const cache = createTileCache();
  cache.record('precipitation', 334); // empty OpenWeatherMap tile
  cache.record('precipitation', 334);
  cache.record('precipitation', 334);
  cache.record('clouds', 4705);       // tile with data
  const status = cache.status();
  assert.deepEqual(status.find((entry) => entry.name === 'precipitation'), { name: 'precipitation', fetched: 3, empty: 3 });
  assert.deepEqual(status.find((entry) => entry.name === 'clouds'), { name: 'clouds', fetched: 1, empty: 0 });
  assert.equal(status.length, 5);
});
