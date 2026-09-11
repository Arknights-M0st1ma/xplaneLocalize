// OpenWeatherMap raster overlays, proxied by the bridge so the API key never
// reaches the iPad. Mirrors windows-bridge/WeatherTiles.cs.
const LAYERS = [
  { name: 'clouds', upstream: 'clouds_new', label: '云' },
  { name: 'precipitation', upstream: 'precipitation_new', label: '降水' },
  { name: 'wind', upstream: 'wind_new', label: '风' },
  { name: 'temperature', upstream: 'temp_new', label: '温度' },
  { name: 'pressure', upstream: 'pressure_new', label: '气压' }
];

export const WEATHER_MAX_ZOOM = 12;

export function weatherLayers() {
  return LAYERS.map(({ name, label }) => ({ name, label }));
}

export function upstreamName(name) {
  return LAYERS.find((layer) => layer.name === name)?.upstream ?? null;
}

export function validCoordinate(name, z, x, y) {
  if (!upstreamName(name)) return false;
  if (!Number.isInteger(z) || z < 0 || z > WEATHER_MAX_ZOOM) return false;
  const limit = 2 ** z;
  return Number.isInteger(x) && Number.isInteger(y) && x >= 0 && x < limit && y >= 0 && y < limit;
}

export function upstreamUrl(name, apiKey, z, x, y) {
  const base = (process.env.EFB_OWM_BASE ?? 'https://tile.openweathermap.org/map').replace(/\/+$/, '');
  return `${base}/${upstreamName(name)}/${z}/${x}/${y}.png?appid=${encodeURIComponent(apiKey)}`;
}

// Small in-memory cache (10 minutes) plus a per-minute request budget, so the
// free plan is never hammered while panning.
export function createTileCache({ maxEntries = 400, ttlMs = 600000, maxPerMinute = 50, now = () => Date.now() } = {}) {
  const entries = new Map();
  const counters = new Map();
  let windowStart = now();
  let windowCount = 0;
  // Measured on 2026-09-11: an empty OpenWeatherMap tile is ~334 bytes, a tile
  // with data is 4.7 KB and up.
  const emptyTileBytes = 900;
  return {
    get(key) {
      const found = entries.get(key);
      if (!found) return null;
      if (found.expiresAt <= now()) { entries.delete(key); return null; }
      return found;
    },
    set(key, body, contentType) {
      if (entries.size >= maxEntries) entries.clear();
      entries.set(key, { body, contentType, expiresAt: now() + ttlMs });
    },
    allow() {
      const current = now();
      if (current - windowStart > 60000) { windowStart = current; windowCount = 0; }
      if (windowCount >= maxPerMinute) return false;
      windowCount += 1;
      return true;
    },
    record(layer, byteCount) {
      const counter = counters.get(layer) ?? { fetched: 0, empty: 0 };
      counter.fetched += 1;
      if (byteCount < emptyTileBytes) counter.empty += 1;
      counters.set(layer, counter);
    },
    status() {
      return LAYERS.map((layer) => {
        const counter = counters.get(layer.name) ?? { fetched: 0, empty: 0 };
        return { name: layer.name, fetched: counter.fetched, empty: counter.empty };
      });
    }
  };
}
