// Synthetic Train Sim World 6 telemetry for `npm run demo`.
//
// The real thing comes from the game's local HTTP API, which only exists on the machine running
// TSW6 - not necessarily the machine this project is developed on. This module produces frames in
// the exact shape the bridge publishes for the TSW source, so the train UI (icon, km/h, speed
// limits, signal, the panels that must be hidden) can be developed and reviewed without the game.
//
// Enable with EFB_DEMO_SOURCE=tsw (or --demo with that variable set).
//
// The route is a synthetic westerly track: it exercises the drawing code but is NOT a real
// railway and must never be read as one. Values are deliberately plausible rather than accurate:
// a train that accelerates, cruises, slows for a lower limit and stops at a station.

export const TSW_DEMO_PROTOCOL = 'TSW-API';

// A short made-up line, roughly west-bound, with enough curvature to look like track.
const ROUTE = [
  { lat: 51.5310, lon: -0.1240 },
  { lat: 51.5245, lon: -0.1830 },
  { lat: 51.5160, lon: -0.2450 },
  { lat: 51.5098, lon: -0.3120 },
  { lat: 51.5085, lon: -0.3790 },
  { lat: 51.5140, lon: -0.4430 },
  { lat: 51.5252, lon: -0.5060 },
  { lat: 51.5420, lon: -0.5640 },
  { lat: 51.5625, lon: -0.6180 },
  { lat: 51.5850, lon: -0.6690 },
  { lat: 51.6098, lon: -0.7130 },
  { lat: 51.6375, lon: -0.7540 }
];

const METRES_PER_DEGREE = 111320;
// Speed limits in km/h, in order along the route. The driver aid reports the one in force and the
// next change ahead of the train, exactly like the game does.
const LIMIT_PLAN = [60, 60, 125, 125, 125, 125, 100, 100, 80, 80, 40, 0];

function distanceBetween(a, b) {
  const dLat = (b.lat - a.lat) * METRES_PER_DEGREE;
  const dLon = (b.lon - a.lon) * METRES_PER_DEGREE * Math.cos((a.lat * Math.PI) / 180);
  return Math.sqrt(dLat * dLat + dLon * dLon);
}

const LEGS = ROUTE.slice(1).map((point, index) => distanceBetween(ROUTE[index], point));
const TOTAL = LEGS.reduce((sum, value) => sum + value, 0);

/** Position at `metres` along the route, clamped to the ends. */
function positionAt(metres) {
  let travelled = 0;
  for (let index = 0; index < LEGS.length; index += 1) {
    const leg = LEGS[index];
    if (metres <= travelled + leg) {
      const ratio = leg === 0 ? 0 : (metres - travelled) / leg;
      const from = ROUTE[index];
      const to = ROUTE[index + 1];
      return {
        lat: from.lat + (to.lat - from.lat) * ratio,
        lon: from.lon + (to.lon - from.lon) * ratio,
        legIndex: index
      };
    }
    travelled += leg;
  }
  const last = ROUTE[ROUTE.length - 1];
  return { lat: last.lat, lon: last.lon, legIndex: LEGS.length - 1 };
}

/** The limit in force at `metres`, and how far the next change is. */
function limitsAt(metres) {
  let travelled = 0;
  let current = LIMIT_PLAN[0];
  for (let index = 0; index < LEGS.length; index += 1) {
    const leg = LEGS[index];
    const limit = LIMIT_PLAN[index] ?? current;
    if (metres < travelled + leg) {
      return { current: limit, next: LIMIT_PLAN[index + 1] ?? limit, distanceToNext: travelled + leg - metres };
    }
    travelled += leg;
    current = limit;
  }
  return { current: 0, next: 0, distanceToNext: 0 };
}

/**
 * Builds the demo generator. Each call to `frame()` advances the simulation by `dtMs` and returns
 * one telemetry object. The run restarts with a fresh departure when it reaches the end of the line.
 */
export function createTswDemo({ speedFactor = 4 } = {}) {
  // Comfortable railway-ish accelerations. The braking rate is what the safe-speed calculation uses,
  // so it has to stay in sync with the deceleration actually applied below.
  const ACCELERATION = 0.55;
  const BRAKING = 0.9;

  // Simulated time runs faster than real time so a full run is watchable in a couple of minutes.
  let metres = 0;
  let metresPerSecond = 0;

  /**
   * Highest speed from which the train can still slow to `target` before covering `distance`.
   * v² = u² + 2as, so the largest safe u is sqrt(target² + 2·a·s).
   *
   * Without this the demo happily ran well past a lower limit ahead of it (an earlier version
   * reported 643 km/h in a 40 zone), which would have hidden real front-end bugs behind nonsense.
   */
  function safeSpeed(target, distance) {
    if (distance <= 0) return target;
    return Math.sqrt(Math.max(0, target * target + 2 * BRAKING * distance));
  }

  function frame(dtMs) {
    // A stalled event loop must not teleport the train: cap the step at half a second of simulated
    // time, which is also what keeps the Euler integration above stable.
    const step = Math.min(2, (dtMs / 1000) * speedFactor);
    const limits = limitsAt(metres);

    // Brake for the limit ahead before reaching it, then run up to the limit in force.
    const immediate = limits.next < limits.current
      ? safeSpeed(limits.next / 3.6, limits.distanceToNext)
      : limits.next / 3.6;
    const target = Math.min(limits.current / 3.6, immediate);

    if (metresPerSecond < target) metresPerSecond = Math.min(target, metresPerSecond + ACCELERATION * step);
    else metresPerSecond = Math.max(target, metresPerSecond - BRAKING * step);
    if (limits.current === 0 && metresPerSecond < 0.4) metresPerSecond = 0;

    metres += metresPerSecond * step;
    if (metres >= TOTAL) {
      // Restart with a fresh departure instead of freezing at the buffer stop.
      metres = 0;
      metresPerSecond = 0;
    }

    const at = positionAt(metres);
    const kmh = metresPerSecond * 3.6;
    const signalDistance = Math.max(0, 1400 - ((metres + 700) % 2800));
    return {
      latitude: at.lat,
      longitude: at.lon,
      groundSpeedKmh: Math.round(kmh * 10) / 10,
      groundSpeedKt: Math.round(metresPerSecond * 1.943844 * 10) / 10,
      limitKmh: limits.current,
      nextLimitKmh: limits.next,
      distanceToNextLimitM: Math.round(limits.distanceToNext),
      distanceToSignalM: Math.round(signalDistance),
      signalAspect: signalDistance > 900 ? 'Clear' : signalDistance > 300 ? 'Caution' : 'Danger',
      gradient: Math.round(Math.sin(metres / 900) * 12 * 10) / 10,
      currentServiceName: '1A23 伦敦 → 雷丁（演示线路）',
      playerProfileName: 'demo',
      source: 'demo',
      apiUrl: 'demo://tsw',
      protocol: TSW_DEMO_PROTOCOL,
      receivedAt: Date.now()
    };
  }

  return { frame, get travelledMetres() { return metres; }, get totalMetres() { return TOTAL; } };
}
