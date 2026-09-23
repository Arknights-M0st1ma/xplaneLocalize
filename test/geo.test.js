import test from 'node:test';
import assert from 'node:assert/strict';
import { nmBetween, metresBetween } from '../public/geo.js';

// Reference distances: 1 degree of latitude is 60 NM by definition, and a
// degree of longitude shrinks with the cosine of the latitude.
test('one degree of latitude is 60 NM', () => {
  assert.ok(Math.abs(nmBetween([0, 0], [1, 0]) - 60) < 0.1);
  assert.ok(Math.abs(nmBetween([45, 10], [46, 10]) - 60) < 0.1);
});

test('a degree of longitude follows the cosine of the latitude', () => {
  assert.ok(Math.abs(nmBetween([0, 0], [0, 1]) - 60) < 0.05);
  assert.ok(Math.abs(nmBetween([60, 0], [60, 1]) - 30) < 0.05);
});

test('a real city pair matches the published distance', () => {
  // Shanghai Pudong to Tokyo Narita: 970 NM great circle.
  const shanghai = [31.1434, 121.8052];
  const tokyo = [35.7647, 140.3864];
  assert.ok(Math.abs(nmBetween(shanghai, tokyo) - 970) < 12, `got ${nmBetween(shanghai, tokyo)}`);
});

test('distance is symmetric and zero for a point against itself', () => {
  const a = [22.308, 113.9185];
  const b = [-33.9461, 151.1772];
  assert.equal(nmBetween(a, a), 0);
  assert.ok(Math.abs(nmBetween(a, b) - nmBetween(b, a)) < 1e-9);
});

test('metres and nautical miles agree', () => {
  const a = [31.1434, 121.8052];
  const b = [31.15, 121.81];
  assert.ok(Math.abs(metresBetween(a, b) - nmBetween(a, b) * 1852) < 1e-6);
  assert.ok(metresBetween(a, b) > 800 && metresBetween(a, b) < 950, `got ${metresBetween(a, b)}`);
});

test('a 700 NM leg is not reported as 12 NM', () => {
  // The regression this module exists for: a mid-Pacific position on a long
  // route must not look like it is about to arrive.
  const sadli = [32.1, 123.5];
  const toshi = [34.5, 138.0];
  const leg = nmBetween(sadli, toshi);
  assert.ok(leg > 600 && leg < 800, `got ${leg}`);
});
