import test from 'node:test';
import assert from 'node:assert/strict';
import { TRACK_OPTIONS, TRACK_RENDER_CAP, decimateTrack, pruneTrackPoints, trackLimits } from '../public/track-retention.js';

const point = (index, minutesAgo) => ({ lat: 50 + index / 10000, lng: 8 + index / 10000, t: minutesAgo * 60000 });

function straight(count, stepMs = 1000) {
  const start = 1_700_000_000_000;
  return Array.from({ length: count }, (_, index) => ({ lat: 50 + index / 10000, lng: 8 + index / 10000, t: start + index * stepMs }));
}

test('point limited modes trim to the configured number of points', () => {
  const points = straight(5000);
  const now = points[points.length - 1].t;
  assert.equal(pruneTrackPoints(points, 'points2000', now).length, 2000);
  assert.equal(pruneTrackPoints(points, 'points10000', now).length, 5000, 'under the limit nothing is dropped');
});

test('time limited modes drop the oldest points only', () => {
  const points = straight(600, 60000); // one point per minute
  const now = points[points.length - 1].t;
  const kept = pruneTrackPoints(points, 'hours1', now);
  // 61 samples cover one hour inclusive of both ends.
  assert.equal(kept.length, 61);
  assert.equal(kept[kept.length - 1], points[points.length - 1], 'the newest point always survives');
  assert.ok(kept[0].t >= now - 3600_000);
});

test('forever keeps everything until the vertex budget and then decimates', () => {
  const points = straight(3000);
  const now = points[points.length - 1].t;
  assert.equal(pruneTrackPoints(points, 'forever', now).length, 3000, 'well under the cap: nothing is lost');

  const huge = straight(40000);
  const thinned = pruneTrackPoints(huge, 'forever', huge[huge.length - 1].t);
  assert.ok(thinned.length <= TRACK_RENDER_CAP, `expected ${thinned.length} <= ${TRACK_RENDER_CAP}`);
  assert.ok(thinned.length > TRACK_RENDER_CAP / 2, 'a long flight must keep far more than the old 2000 point limit');
  assert.equal(thinned[0], huge[0], 'the start of the flight is kept');
  assert.equal(thinned[thinned.length - 1], huge[huge.length - 1], 'the newest point is kept');
  for (let index = 1; index < thinned.length; index += 1) {
    assert.ok(thinned[index].t >= thinned[index - 1].t, 'the track stays in order');
  }
});

test('decimateTrack is a no-op below the cap and never reorders', () => {
  const points = straight(100);
  assert.equal(decimateTrack(points, 200), points);
  const source = straight(1000);
  const thinned = decimateTrack(source, 200);
  assert.ok(thinned.length <= 200);
  assert.equal(thinned[0], source[0]);
  assert.equal(thinned[thinned.length - 1], source[source.length - 1]);
});

test('unknown options fall back to the forever policy', () => {
  assert.equal(trackLimits('nonsense'), TRACK_OPTIONS.forever);
  assert.equal(trackLimits(undefined), TRACK_OPTIONS.forever);
  assert.equal(trackLimits('hours1').mode, 'time');
});

test('a track longer than the old 2000 point cap is preserved', () => {
  // Regression: the EFB used to keep only 2000 points (~4 minutes at cruise).
  const points = straight(12000);
  const kept = pruneTrackPoints(points, 'forever', points[points.length - 1].t);
  assert.ok(kept.length > 2000, `expected more than 2000 points, got ${kept.length}`);
});
