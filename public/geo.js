// Great-circle distance helpers.
//
// Pure functions: app.js owns the map and the DOM, this module owns the maths so
// the units can be regression tested without a browser (see test/geo.test.js).
//
// [lat, lng] in degrees in, nautical miles (or metres) out.

const RAD = Math.PI / 180;
export const NM_PER_METRE = 1 / 1852;
export const EARTH_RADIUS_NM = 3440.065;

/**
 * Great-circle distance between two [lat, lng] points, in nautical miles.
 *
 * This used to convert to radians and then multiply by 60 (the number of
 * nautical miles in a degree of latitude), which made every distance 57.3x too
 * small: the flight card reported "10 NM" and "0h01m" to run wherever the
 * aircraft actually was.
 */
export function nmBetween(a, b) {
  const lat1 = a[0] * RAD;
  const lat2 = b[0] * RAD;
  const dLat = lat2 - lat1;
  const dLon = (b[1] - a[1]) * RAD;
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(lat1) * Math.cos(lat2) * Math.sin(dLon / 2) ** 2;
  return 2 * EARTH_RADIUS_NM * Math.asin(Math.min(1, Math.sqrt(h)));
}

/** Same distance in metres, for the small "has it moved?" comparisons. */
export function metresBetween(a, b) {
  return nmBetween(a, b) / NM_PER_METRE;
}
