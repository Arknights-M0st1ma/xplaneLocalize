const DATA_RECORD_BYTES = 36;

const finite = (value) => Number.isFinite(value) && Math.abs(value) < 1e20 ? value : null;

/** Parse X-Plane DATA UDP packets. Values from different rows are merged by the bridge. */
export function parseDataPacket(buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length < 5) return null;
  const header = buffer.subarray(0, 5).toString('ascii');
  if (!header.startsWith('DATA')) return null;

  const records = {};
  for (let offset = 5; offset + DATA_RECORD_BYTES <= buffer.length; offset += DATA_RECORD_BYTES) {
    const index = buffer.readInt32LE(offset);
    if (index < 0 || index > 10000) continue;
    const values = [];
    for (let i = 0; i < 8; i += 1) values.push(finite(buffer.readFloatLE(offset + 4 + i * 4)));
    records[index] = values;
  }
  return Object.keys(records).length ? records : null;
}

/**
 * Known X-Plane 11/12 Data Output row mappings. Unknown/missing rows are ignored.
 * Configure X-Plane to send rows 3, 4, 17 and 20. Row labels are the authority in
 * the simulator UI; numeric indexes can change in future versions.
 */
export function recordsToTelemetry(records) {
  const result = {};
  const speeds = records[3];
  if (speeds) {
    result.indicatedAirspeedKt = finite(speeds[0]);
    result.trueAirspeedKt = finite(speeds[2]);
    result.groundSpeedKt = finite(speeds[3]);
  }

  const vertical = records[4];
  if (vertical) {
    result.mach = finite(vertical[0]);
    result.verticalSpeedFpm = finite(vertical[1]);
    result.gLoad = finite(vertical[4]);
  }

  const attitude = records[17];
  if (attitude) {
    result.pitchDeg = finite(attitude[0]);
    result.rollDeg = finite(attitude[1]);
    result.headingTrueDeg = finite(attitude[2]);
    result.headingMagDeg = finite(attitude[3]);
  }

  const position = records[20];
  if (position) {
    const lat = finite(position[0]);
    const lon = finite(position[1]);
    if (lat !== null && lat >= -90 && lat <= 90) result.latitude = lat;
    if (lon !== null && lon >= -180 && lon <= 180) result.longitude = lon;
    result.altitudeMslFt = finite(position[2]);
    result.altitudeAglFt = finite(position[3]);
  }
  return Object.fromEntries(Object.entries(result).filter(([, value]) => value !== null));
}

export function parsePacket(buffer) {
  const records = parseDataPacket(buffer);
  return records ? { protocol: 'DATA', fields: recordsToTelemetry(records), records } : null;
}
