import fs from 'node:fs';
import path from 'node:path';

function loadDotEnv(file = path.resolve('.env')) {
  if (!fs.existsSync(file)) return;
  for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
    const match = line.match(/^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$/);
    if (!match || process.env[match[1]] !== undefined) continue;
    let value = match[2];
    if ((value.startsWith('"') && value.endsWith('"')) ||
        (value.startsWith("'") && value.endsWith("'"))) value = value.slice(1, -1);
    process.env[match[1]] = value;
  }
}

function port(name, fallback) {
  const value = Number(process.env[name] ?? fallback);
  if (!Number.isInteger(value) || value < 1 || value > 65535) {
    throw new Error(`${name} must be an integer from 1 to 65535`);
  }
  return value;
}

loadDotEnv();

export const config = Object.freeze({
  udpHost: process.env.XPLANE_UDP_HOST || '0.0.0.0',
  udpPort: port('XPLANE_UDP_PORT', 49000),
  sourceIp: process.env.XPLANE_SOURCE_IP || '',
  webHost: process.env.EFB_HOST || '0.0.0.0',
  webPort: port('EFB_PORT', 8080),
  demo: process.argv.includes('--demo') || process.env.EFB_DEMO === '1',
  tlsCert: process.env.EFB_TLS_CERT || '',
  tlsKey: process.env.EFB_TLS_KEY || '',
  authorizedTileUrl: process.env.AUTHORIZED_CHART_TILE_URL || '',
  authorizedTileAttribution: process.env.AUTHORIZED_CHART_ATTRIBUTION || '',
  customBaseMapName: process.env.CUSTOM_BASE_MAP_NAME || '',
  customBaseMapUrl: process.env.CUSTOM_BASE_MAP_URL || '',
  customBaseMapAttribution: process.env.CUSTOM_BASE_MAP_ATTRIBUTION || '',
  navigraphExternalUrl: process.env.NAVIGRAPH_EXTERNAL_URL || 'https://charts.navigraph.com/'
});
