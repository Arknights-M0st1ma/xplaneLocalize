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
  // Demo telemetry shape: "plane" (default) or "tsw" for Train Sim World frames. The dev server
  // never talks to the real game API - that lives in the Windows exe - so this is how the train
  // front end is developed without the game installed.
  demoSource: (process.env.EFB_DEMO_SOURCE || 'plane').trim().toLowerCase() === 'tsw' ? 'tsw' : 'xp',
  tlsCert: process.env.EFB_TLS_CERT || '',
  tlsKey: process.env.EFB_TLS_KEY || '',
  customBaseMapName: process.env.CUSTOM_BASE_MAP_NAME || '',
  customBaseMapUrl: process.env.CUSTOM_BASE_MAP_URL || '',
  customBaseMapAttribution: process.env.CUSTOM_BASE_MAP_ATTRIBUTION || '',
  // SimBrief Pilot ID (1-7 digits) or Navigraph alias. Empty disables the feature.
  simbriefUser: process.env.SIMBRIEF_USER || '',
  simbriefEndpoint: process.env.SIMBRIEF_API_URL || '',
  // OpenWeatherMap API key for the optional weather overlays (exe keeps this in
  // its own config file; the dev server reads it from the environment).
  weatherApiKey: process.env.OWM_API_KEY || '',
  // Folder of PDF manuals served to the EFB reader (the iPad cannot read the
  // PC's disk itself).
  manualFolder: process.env.EFB_MANUAL_FOLDER || ''
});
