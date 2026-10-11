#!/usr/bin/env node
// Builds appPackage/build/appPackage.zip (manifest + icons) for sideloading into Teams.
// Values come from bot/.env, overridable by environment variables:
//   CLIENT_ID       -> bots[0].botId and webApplicationInfo.id
//   BOT_APP_ID_URI  -> webApplicationInfo.resource (default api://botid-<CLIENT_ID>)
//   TEAMS_APP_ID    -> manifest id (default: CLIENT_ID)
import { mkdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { crc32, deflateRawSync } from 'node:zlib';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const appDir = join(root, 'appPackage');
const outDir = join(appDir, 'build');

const env = { ...readDotEnv(join(root, 'bot', '.env')), ...process.env };
const botId = env.CLIENT_ID;
if (!botId) {
  console.error('CLIENT_ID is not set (bot/.env or environment).');
  process.exit(1);
}
const values = {
  BOT_ID: botId,
  BOT_APP_ID_URI: env.BOT_APP_ID_URI || `api://botid-${botId}`,
  TEAMS_APP_ID: env.TEAMS_APP_ID || botId,
};

const manifest = readFileSync(join(appDir, 'manifest.json'), 'utf8').replace(/\$\{\{(\w+)\}\}/g, (match, name) => {
  if (!(name in values)) throw new Error(`No value for ${match} in manifest.json`);
  return values[name];
});
JSON.parse(manifest); // fail fast on broken JSON

mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, 'manifest.json'), manifest);
const zipPath = join(outDir, 'appPackage.zip');
writeFileSync(
  zipPath,
  zip([
    ['manifest.json', Buffer.from(manifest)],
    ['color.png', readFileSync(join(appDir, 'color.png'))],
    ['outline.png', readFileSync(join(appDir, 'outline.png'))],
  ]),
);
console.log(`Wrote ${zipPath}`);
console.log(`  botId / webApplicationInfo.id : ${values.BOT_ID}`);
console.log(`  webApplicationInfo.resource   : ${values.BOT_APP_ID_URI}`);

function readDotEnv(path) {
  if (!existsSync(path)) return {};
  const out = {};
  for (const line of readFileSync(path, 'utf8').split(/\r?\n/)) {
    const m = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$/.exec(line);
    if (m && !line.trimStart().startsWith('#')) out[m[1]] = m[2].replace(/^(['"])(.*)\1$/, '$2');
  }
  return out;
}

/** Minimal ZIP writer (deflate, no directories) - enough for a Teams app package. */
function zip(entries) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const [name, data] of entries) {
    const nameBuf = Buffer.from(name);
    const compressed = deflateRawSync(data);
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4); // version needed
    local.writeUInt16LE(0, 6); // flags
    local.writeUInt16LE(8, 8); // deflate
    local.writeUInt32LE(0, 10); // time/date
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(compressed.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    local.writeUInt16LE(0, 28);
    locals.push(local, nameBuf, compressed);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4); // version made by
    central.writeUInt16LE(20, 6); // version needed
    central.writeUInt16LE(0, 8);
    central.writeUInt16LE(8, 10);
    central.writeUInt32LE(0, 12);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(compressed.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBuf.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, nameBuf);
    offset += local.length + nameBuf.length + compressed.length;
  }
  const centralBuf = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralBuf.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, centralBuf, end]);
}
