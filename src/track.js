// Flown-track recorder for the bridge (dev server).
//
// The browser used to be the recorder: it appended a point for every telemetry
// frame it happened to be awake for. Backgrounding Safari (or losing the socket
// for a minute) silently punched a hole in the track. The bridge now owns the
// recording, so the track is continuous no matter what the iPad is doing, and
// the browser only reads it back.

import fs from 'node:fs';
import path from 'node:path';
import { nmBetween } from '../public/geo.js';

// Sampling: one point per "interesting" movement. At 450 kt the 0.1 NM step is
// reached in about 0.8 s (roughly 4 000 points/hour, so a six hour flight is
// comfortably inside the cap); a parked aircraft still gets a 5 s heartbeat so
// the shape of a stop is preserved.
export const MIN_GAP_MS = 700;
export const MIN_MOVE_NM = 0.1;
export const HEARTBEAT_MS = 5000;
export const DEFAULT_MAX_POINTS = 40000;

const round = (value, digits) => {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
};

export function createTrackStore({
  maxPoints = DEFAULT_MAX_POINTS,
  savePath = '',
  saveIntervalMs = 30000,
  minGapMs = MIN_GAP_MS,
  minMoveNm = MIN_MOVE_NM,
  heartbeatMs = HEARTBEAT_MS,
  now = () => Date.now()
} = {}) {
  let points = [];        // { lat, lng, t }
  let dirty = false;
  let savedAt = 0;

  function load() {
    if (!savePath) return 0;
    try {
      const raw = fs.readFileSync(savePath, 'utf8');
      const parsed = JSON.parse(raw);
      if (!Array.isArray(parsed)) return 0;
      points = parsed
        .filter((point) => Array.isArray(point) && point.length >= 3
          && Number.isFinite(point[0]) && Number.isFinite(point[1]) && Number.isFinite(point[2]))
        .map((point) => ({ lat: point[0], lng: point[1], t: point[2] }))
        .sort((a, b) => a.t - b.t);
      if (points.length > maxPoints) points.splice(0, points.length - maxPoints);
      return points.length;
    } catch {
      return 0;   // a missing or corrupt file must never stop the bridge
    }
  }

  function save(force = false) {
    if (!savePath || !dirty) return false;
    const at = now();
    if (!force && at - savedAt < saveIntervalMs) return false;
    savedAt = at;
    dirty = false;
    const payload = JSON.stringify(points.map((point) => [round(point.lat, 5), round(point.lng, 5), point.t]));
    try {
      fs.mkdirSync(path.dirname(savePath), { recursive: true });
      const temporary = `${savePath}.tmp`;
      fs.writeFileSync(temporary, payload, 'utf8');
      fs.renameSync(temporary, savePath);
      return true;
    } catch {
      dirty = true;
      return false;
    }
  }

  /** Appends a telemetry position. Returns true when a point was stored. */
  function record(position, at = now()) {
    const lat = Number(position?.[0]);
    const lng = Number(position?.[1]);
    if (!Number.isFinite(lat) || !Number.isFinite(lng) || (lat === 0 && lng === 0)) return false;
    if (lat < -90 || lat > 90 || lng < -180 || lng > 180) return false;
    const last = points[points.length - 1];
    if (last) {
      const elapsed = at - last.t;
      if (elapsed < minGapMs) return false;
      if (elapsed < heartbeatMs && nmBetween([last.lat, last.lng], [lat, lng]) < minMoveNm) return false;
    }
    points.push({ lat, lng, t: at });
    dirty = true;
    if (points.length > maxPoints) points.splice(0, points.length - maxPoints);
    return true;
  }

  function clear() {
    const had = points.length;
    points = [];
    dirty = true;
    savedAt = 0;
    save(true);
    return had;
  }

  /**
   * Points the client asked for. `since` (ms) makes the response incremental,
   * which is what lets a backgrounded iPad catch up without re-downloading the
   * whole flight. When the older points were dropped the client is told to
   * replace its copy instead of appending to a track with a hole in it.
   *
   * The retention policy itself stays in the page (track-retention.js): the
   * bridge records everything it is allowed to keep, and the page decides how
   * much of that to draw.
   */
  function read({ since = null } = {}) {
    const first = points.length > 0 ? points[0].t : null;
    const incremental = Number.isFinite(since) && first !== null && since >= first;
    const selected = incremental ? points.filter((point) => point.t > since) : points;
    return {
      points: selected.map((point) => [round(point.lat, 5), round(point.lng, 5), point.t]),
      replaced: !incremental,
      first,
      last: points.length > 0 ? points[points.length - 1].t : null,
      count: points.length
    };
  }

  return {
    record,
    read,
    clear,
    load,
    save,
    get size() { return points.length; },
    get dirty() { return dirty; }
  };
}
