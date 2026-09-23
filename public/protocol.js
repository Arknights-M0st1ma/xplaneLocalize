// Which simulator is feeding the page, and what that means for the UI.
//
// The bridge can drive the map from either X-Plane 12 (UDP `DATA` packets, `protocol: "DATA"`)
// or Train Sim World 6 (its local HTTP API, `protocol: "TSW-API"`). The two sources carry
// different fields and make different panels meaningful, so the decisions live here as pure
// functions: app.js owns the DOM and the Leaflet layers, and this part is unit testable
// without a browser (see test/protocol.test.js).

export const SOURCE_XPLANE = 'xp';
export const SOURCE_TSW = 'tsw';
export const TSW_PROTOCOL = 'TSW-API';

// 1 m/s = 3.6 km/h; the game reports every speed in m/s.
export const MS_TO_KMH = 3.6;
export const MS_TO_KT = 1.943844;

/**
 * Normalises everything we know about the data source into one answer.
 *
 * `protocol` wins over the configured source because it describes the frame actually in hand:
 * while the bridge is restarting, or before the first frame arrives, the configuration is the
 * only hint available, and a stale protocol string from the previous source would otherwise
 * keep the train UI on screen after switching back to X-Plane.
 */
export function sourceInfo(protocol, configured) {
  const isTrain = protocol === TSW_PROTOCOL
    ? true
    : protocol === '' || protocol === undefined || protocol === null
      ? configured === SOURCE_TSW
      : false;
  return { isTrain, protocol: protocol ?? '' };
}

/** Units and labels for the four instruments. X-Plane keeps knots/feet; the train reads km/h. */
export function instrumentLabels(isTrain) {
  return isTrain
    ? { speed: 'KM/H', altitude: '限速 KM/H', heading: '', vertical: 'M' }
    : { speed: 'KT', altitude: 'FT MSL', heading: 'TRUE', vertical: 'FPM' };
}

/**
 * Primary speed for the big readout. The bridge sends `groundSpeedKmh` for the train and
 * `groundSpeedKt` for the aircraft, but either frame may be partial, so every candidate is tried.
 */
export function primarySpeed(data, isTrain) {
  if (!data) return null;
  const candidates = isTrain
    ? [data.groundSpeedKmh, data.groundSpeedKt === undefined ? undefined : data.groundSpeedKt * 1.852]
    : [data.groundSpeedKt, data.trueAirspeedKt, data.indicatedAirspeedKt];
  for (const value of candidates) {
    if (Number.isFinite(value)) return value;
  }
  return null;
}

/**
 * The altitude instrument shows the active speed limit in train mode and the speed-limit
 * distance in the vertical-speed slot. Both are absent whenever the driver-aid endpoint is not
 * available, and the caller renders "—" for null.
 */
export function secondaryReadouts(data, isTrain) {
  if (!isTrain) {
    return { altitude: data?.altitudeMslFt ?? null, vertical: data?.verticalSpeedFpm ?? null };
  }
  return {
    altitude: Number.isFinite(data?.limitKmh) ? data.limitKmh : null,
    vertical: Number.isFinite(data?.distanceToNextLimitM) ? data.distanceToNextLimitM : null
  };
}

/** Panel visibility. SimBrief routes, the airport ground layer and airline badges are all
 * aviation-only concepts, so they are hidden rather than shown empty. */
export function panelVisibility(isTrain) {
  return {
    simbrief: !isTrain,
    ground: !isTrain,
    airline: !isTrain,
    headingInstrument: !isTrain,
    // Weather is real weather, which matters just as much to a driver.
    weather: true
  };
}

/** Status pill text. `kind` matches the CSS classes already used by setStatus(). */
export function statusText(kind, isTrain) {
  if (kind === 'live') return isTrain ? '列车数据实时' : '数据实时';
  if (kind === 'lost') return isTrain ? 'TSW 数据超时' : '数据超时';
  if (kind === 'waiting') return isTrain ? '等待 TSW 数据' : '等待数据';
  return '';
}

/** Human-readable distance for the driver-aid readout, in metres below 1 km. */
export function formatMetres(value) {
  if (!Number.isFinite(value)) return '—';
  if (value >= 1000) return `${(value / 1000).toFixed(value >= 10000 ? 0 : 1)} km`;
  return `${Math.round(value)} m`;
}

/** Flight phase is avionics-only; a train gets a simpler ground/rolling split. */
export function trainPhase(groundSpeedKmh) {
  if (!Number.isFinite(groundSpeedKmh)) return '—';
  if (groundSpeedKmh < 1) return '停车';
  if (groundSpeedKmh < 5) return '调车';
  return '运行';
}
