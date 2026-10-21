#!/usr/bin/env node
// Development-only demo data: creates a dev admin account and one on-sale event through the REAL HTTP API.
// Idempotent: does nothing when an event already exists. Never run against anything but the local dev stack.
//   node scripts/dev-seed.mjs            (reads .env; API_PORT/WEB_PORT/ADMIN_* values)
import { execFileSync } from 'node:child_process';
import { appendFileSync, existsSync, readFileSync } from 'node:fs';
import { randomBytes } from 'node:crypto';

const ENV_FILE = new URL('../.env', import.meta.url);
const COMPOSE = ['compose', '-f', 'compose.dev.yaml'];
const env = () => Object.fromEntries(readFileSync(ENV_FILE, 'utf8').split(/\r?\n/)
  .filter((l) => l.includes('=') && !l.trim().startsWith('#')).map((l) => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)]));

if (!existsSync(ENV_FILE)) throw new Error('.env missing: run the setup command first');
let cfg = env();
if (!cfg.DEV_ADMIN_PASSWORD) {
  appendFileSync(ENV_FILE, `\nDEV_ADMIN_EMAIL=admin@pulsereserve.test\nDEV_ADMIN_PASSWORD=${randomBytes(18).toString('hex')}\n`);
  cfg = env();
}
const base = `http://localhost:${cfg.API_PORT ?? 5080}`;
const email = cfg.DEV_ADMIN_EMAIL;
const password = cfg.DEV_ADMIN_PASSWORD;

const jar = new Map();
let csrf = '';
async function call(method, path, body, { allowStatus = [] } = {}) {
  const res = await fetch(base + path, {
    method,
    headers: {
      'content-type': 'application/json',
      cookie: [...jar].map(([k, v]) => `${k}=${v}`).join('; '),
      ...(csrf ? { 'x-xsrf-token': csrf } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  for (const c of res.headers.getSetCookie()) {
    const [pair] = c.split(';');
    const i = pair.indexOf('=');
    jar.set(pair.slice(0, i), pair.slice(i + 1));
  }
  const text = await res.text();
  const json = text ? JSON.parse(text) : null;
  if (json?.csrfToken) csrf = json.csrfToken;
  if (!res.ok && !allowStatus.includes(res.status)) {
    throw new Error(`${method} ${path} -> ${res.status} ${json?.title ?? ''} ${json?.detail ?? ''}`);
  }
  return { status: res.status, json };
}
const id = (v) => (v && typeof v === 'object' ? (v.value ?? v.id) : v);

await call('GET', '/api/identity/session');
let session = (await call('POST', '/api/identity/login', { email, password }, { allowStatus: [401] }));
if (session.status === 401) {
  session = await call('POST', '/api/identity/register', { email, password, displayName: 'Dev Admin' });
}
if (!session.json.isAdmin) {
  const adminId = session.json.user.id;
  console.log('Registering the dev admin as Admin (API container is recreated with Identity__AdminUserIds)...');
  if (!/^ADMIN_USER_IDS=/m.test(readFileSync(ENV_FILE, 'utf8'))) appendFileSync(ENV_FILE, `\nADMIN_USER_IDS=${adminId}\n`);
  execFileSync('docker', [...COMPOSE, 'up', '-d', '--force-recreate', '--wait', 'api'], { stdio: 'inherit' });
  await call('GET', '/api/identity/session');
  session = await call('POST', '/api/identity/login', { email, password });
  if (!session.json.isAdmin) throw new Error('User is still not an admin; check ADMIN_USER_IDS in .env');
}

const existing = (await call('GET', '/api/catalog/events')).json;
if (existing.length > 0) {
  console.log(`Events already exist (${existing.length}); nothing to seed.`);
  process.exit(0);
}

const venue = id((await call('POST', '/api/catalog/venues', {
  name: 'Pulse Arena', street: '1 Harbour Road', city: 'Cairo', country: 'EG', postalCode: '11511',
})).json.id);
const layoutId = id((await call('POST', '/api/catalog/seating-layouts', { venueId: venue, name: 'Main Hall' })).json.id);
const layout = async () => (await call('GET', `/api/catalog/seating-layouts/${layoutId}`)).json;

for (const name of ['Premium', 'Standard']) await call('POST', `/api/catalog/seating-layouts/${layoutId}/price-tiers`, { name });
const tiers = Object.fromEntries((await layout()).priceTiers.map((t) => [t.name, id(t.id)]));

const sections = [
  { name: 'Orchestra', tier: 'Premium', rows: ['A', 'B', 'C'] },
  { name: 'Balcony', tier: 'Standard', rows: ['D', 'E', 'F'] },
];
for (const [i, s] of sections.entries()) await call('POST', `/api/catalog/seating-layouts/${layoutId}/sections`, { name: s.name, displayOrder: i + 1 });
let snapshot = await layout();
const sectionIds = Object.fromEntries(snapshot.sections.map((s) => [s.name, id(s.id)]));
for (const s of sections) {
  for (const [r, label] of s.rows.entries()) {
    await call('POST', `/api/catalog/seating-layouts/${layoutId}/rows`, { sectionId: sectionIds[s.name], label, displayOrder: r + 1 });
  }
}
snapshot = await layout();
const rowIds = Object.fromEntries(snapshot.rows.map((r) => [r.label, { row: id(r.id), section: id(r.sectionId) }]));
let y = 0;
for (const s of sections) {
  for (const label of s.rows) {
    y += 1;
    for (let n = 1; n <= 8; n++) {
      await call('POST', `/api/catalog/seating-layouts/${layoutId}/seats`, {
        rowId: rowIds[label].row, sectionId: rowIds[label].section, label: `${label}${n}`, coordinateX: n, coordinateY: y, priceTierId: tiers[s.tier],
      });
    }
  }
}
await call('POST', `/api/catalog/seating-layouts/${layoutId}/lock`, {});

const start = new Date(Date.now() + 14 * 86_400_000); start.setUTCHours(18, 0, 0, 0);
const end = new Date(start.getTime() + 3 * 3_600_000);
const eventId = id((await call('POST', '/api/catalog/events', {
  name: 'Pulse Live: Opening Night', description: 'Demo event created by scripts/dev-seed.mjs for local development.',
  venueId: venue, scheduledStartUtc: start.toISOString(), scheduledEndUtc: end.toISOString(), seatingLayoutId: layoutId,
})).json.id);
await call('POST', `/api/catalog/events/${eventId}/prices`, { priceTierId: tiers.Premium, priceAmount: 120, currency: 'USD' });
await call('POST', `/api/catalog/events/${eventId}/prices`, { priceTierId: tiers.Standard, priceAmount: 60, currency: 'USD' });
await call('POST', `/api/catalog/events/${eventId}/publish`, {});
await call('POST', `/api/catalog/events/${eventId}/start-sale`, {});
console.log(`Seeded event ${eventId}: 48 seats, on sale. Sign-in: ${email} (password in .env as DEV_ADMIN_PASSWORD).`);
