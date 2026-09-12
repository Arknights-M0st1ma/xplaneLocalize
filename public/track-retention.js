// Retention rules for the flown track.
//
// Pure functions only: app.js owns the Leaflet layers and the browser storage,
// so this part can be unit tested without a DOM (see test/track.test.js).

export const TRACK_OPTIONS = {
  points2000: { mode: 'points', maxPoints: 2000 },
  points10000: { mode: 'points', maxPoints: 10000 },
  points50000: { mode: 'points', maxPoints: 50000 },
  hours1: { mode: 'time', maxHours: 1, maxPoints: 60000 },
  hours6: { mode: 'time', maxHours: 6, maxPoints: 120000 },
  forever: { mode: 'forever', maxPoints: 20000 }
};

// Hard ceiling for what is handed to Leaflet, whatever the option says.
export const TRACK_RENDER_CAP = 24000;
// The newest points always stay at full resolution.
export const TRACK_KEEP_NEWEST = 4000;

export function trackLimits(option) {
  return TRACK_OPTIONS[option] ?? TRACK_OPTIONS.forever;
}

// Keeps the shape of the flight while bounding the vertex count: the newest part
// stays untouched, the older part is thinned by a growing stride. The first and
// last points survive so the line never loses its ends.
export function decimateTrack(points, cap) {
  if (points.length <= cap) return points;
  const newest = Math.min(TRACK_KEEP_NEWEST, Math.floor(cap / 3));
  const older = points.slice(0, points.length - newest);
  const tail = points.slice(points.length - newest);
  let stride = 2;
  while (Math.ceil(older.length / stride) + tail.length > cap) stride *= 2;
  const thinned = [];
  for (let index = 0; index < older.length; index += stride) thinned.push(older[index]);
  if (older.length > 0 && thinned[thinned.length - 1] !== older[older.length - 1]) thinned.push(older[older.length - 1]);
  return thinned.concat(tail);
}

// Applies the selected policy and returns the surviving points.
export function pruneTrackPoints(points, option, now = Date.now()) {
  const limits = trackLimits(option);
  if (limits.mode === 'time') {
    const cutoff = now - limits.maxHours * 3600 * 1000;
    let drop = 0;
    while (drop < points.length && points[drop].t < cutoff) drop += 1;
    const kept = drop > 0 ? points.slice(drop) : points;
    // A densely sampled flight can still exceed the safety cap: thin it instead
    // of cutting the recorded hours short.
    const limit = Math.min(limits.maxPoints ?? TRACK_RENDER_CAP, TRACK_RENDER_CAP);
    return kept.length > limit ? decimateTrack(kept, limit) : kept;
  }
  const limit = Math.min(limits.maxPoints ?? TRACK_RENDER_CAP, TRACK_RENDER_CAP);
  if (limits.mode === 'points') {
    // "Keep the last N points" is meant literally.
    return points.length > limit ? points.slice(points.length - limit) : points;
  }
  // forever: keep the whole flight, thinning the older part to stay drawable.
  return points.length > limit ? decimateTrack(points, limit) : points;
}
