// A pasted list, and which part a line means when two carry the same number.
//
// HOW TO RUN
// ----------
//   node tools/bulk-lookup.mjs
//
// with the .NET API in Development on :5080, signed-in-able as the seeded
// admin. One API: `rows` is the shape the backlog specifies and the Node API
// does not serve it yet.
//
// WHY A HARNESS
// -------------
// The rule worth checking is which of two parts a line resolves to, and it
// only has two to choose from when two part numbers NORMALISE onto each other.
// `Product.partNumber` is unique, so that cannot be arranged by writing the
// same string twice — it needs `ZZ-COLLIDE-1` and `ZZCOLLIDE1`, two different
// values that compare equal once separators are dropped. Building that wants a
// catalogue, which wants a database.
//
// It makes its own two parts and removes them, counting before and after.

import { installCsrf } from './csrf.mjs';

const NET = process.env.NET ?? 'http://localhost:5080';

await installCsrf(NET);

let failures = 0;

const line = (label, detail = '') => console.log(`  ok    ${label}${detail ? `  ${detail}` : ''}`);
const bad = (label, detail) => { failures++; console.log(`  FAIL  ${label}\n        ${detail}`); };
const expect = (label, got, want) =>
  got === want ? line(label, String(got)) : bad(label, `expected ${want}, got ${got}`);

async function call(path, method = 'GET', body, cookie) {
  const res = await fetch(NET + path, {
    method,
    headers: { 'Content-Type': 'application/json', ...(cookie ? { cookie } : {}) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });

  const text = await res.text();
  let parsed;
  try { parsed = JSON.parse(text); } catch { parsed = text; }

  return { status: res.status, body: parsed };
}

const signIn = await call('/api/auth/login', 'POST',
  { email: 'admin@autopartshub.com', password: 'admin123' });

if (signIn.status !== 200) {
  console.error(`could not sign in as the seeded admin: ${signIn.status}`);
  process.exit(1);
}

const admin = (await fetch(`${NET}/api/auth/login`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: 'admin@autopartshub.com', password: 'admin123' }),
})).headers.getSetCookie().map((c) => c.split(';')[0]).join('; ');

// ------------------------------------------------------- two colliding parts

const references = await call('/api/admin/products', 'GET', undefined, admin);
const before = references.body?.products?.length;
const brands = references.body?.manufacturers ?? [];
const system = references.body?.systems?.[0]?.id;

if (brands.length < 2 || !system) {
  console.error('need at least two manufacturers and one vehicle system to run this.');
  process.exit(1);
}

const stamp = Date.now();
// Different strings, same normalised form. This is the only way two parts can
// share a number, because Product.partNumber is unique.
const raw = [`ZZ-COLLIDE-${stamp}`, `ZZCOLLIDE${stamp}`];

const made = [];
for (const [at, partNumber] of raw.entries()) {
  const r = await call('/api/admin/products', 'POST', {
    partNumber,
    name: `Collision probe ${at}`,
    basePrice: 10 + at,
    manufacturerId: brands[at].id,
    vehicleSystemId: system,
  }, admin);

  if (!r.body?.product?.id) {
    console.error('could not create a probe part:', JSON.stringify(r.body));
    for (const done of made) await call(`/api/admin/products/${done.id}`, 'DELETE', undefined, admin);
    process.exit(1);
  }

  made.push({ id: r.body.product.id, brand: brands[at].name, partNumber });
}

console.log(`probe parts ${made.map((m) => `${m.partNumber} (${m.brand})`).join(', ')}\n`);

const typed = `ZZ COLLIDE ${stamp}`;
const rowOf = (res) => res.body?.rows?.[0];

try {
  // --------------------------------------------------------- the old shape

  let r = await call('/api/catalog/bulk', 'POST', { partNumbers: [typed] }, admin);
  expect('partNumbers still answers', r.status, 200);
  expect('  and reports its own cap', r.body?.maxRows, 1000);
  expect('  and finds the number', rowOf(r)?.found, true);

  // --------------------------------------------------------- the new shape

  r = await call('/api/catalog/bulk', 'POST', { rows: [{ partNumber: typed }] }, admin);
  expect('rows answers', r.status, 200);
  expect('  and reports the larger cap', r.body?.maxRows, 2000);
  expect('  and finds the number', rowOf(r)?.found, true);

  // A bare string inside rows is a part number with no brand.
  r = await call('/api/catalog/bulk', 'POST', { rows: [typed] }, admin);
  expect('a bare string in rows is taken as a part number', rowOf(r)?.found, true);

  // ------------------------------------------------------ the brand decides
  //
  // The whole reason `rows` carries a manufacturer. Both probe parts answer
  // this number; without a brand the winner is whichever the planner returned
  // first, and with one it is the one the line named.

  for (const probe of made) {
    r = await call('/api/catalog/bulk', 'POST',
      { rows: [{ partNumber: typed, manufacturer: probe.brand }] }, admin);

    expect(`naming ${probe.brand} returns ${probe.brand}`,
      rowOf(r)?.product?.manufacturer, probe.brand);
  }

  // Loosely compared, because a brand is whatever a spreadsheet had in it.
  r = await call('/api/catalog/bulk', 'POST',
    { rows: [{ partNumber: typed, manufacturer: made[1].brand.toLowerCase() }] }, admin);
  expect('the brand is compared loosely', rowOf(r)?.product?.manufacturer, made[1].brand);

  // A brand nothing recognises still returns the part rather than an empty
  // row — better the part than nothing, and the row says which brand it is.
  r = await call('/api/catalog/bulk', 'POST',
    { rows: [{ partNumber: typed, manufacturer: 'No Such Brand Gmbh' }] }, admin);
  expect('an unknown brand still returns a part', rowOf(r)?.found, true);

  // --------------------------------------------------------------- the caps

  const many = (n) => Array.from({ length: n }, () => ({ partNumber: typed }));

  r = await call('/api/catalog/bulk', 'POST', { rows: many(2100) }, admin);
  expect('2100 rows are cut to the cap', r.body?.submitted, 2000);
  expect('  and the answer says it was cut', r.body?.truncated, true);

  r = await call('/api/catalog/bulk', 'POST', { rows: many(5) }, admin);
  expect('a short list is not marked truncated', r.body?.truncated, false);

  // ----------------------------------------------------------- the body cap
  //
  // Refused by the server before the JSON is parsed, which is the point: the
  // row cap bounds the work and this bounds the reading.

  const huge = { rows: [{ partNumber: typed, manufacturer: 'x'.repeat(3 * 1024 * 1024) }] };
  const oversized = await call('/api/catalog/bulk', 'POST', huge, admin);
  if (oversized.status === 413 || oversized.status === 400) {
    line('a body over two megabytes is refused', String(oversized.status));
  } else {
    bad('a body over two megabytes is refused', `answered ${oversized.status}`);
  }

  // --------------------------------------------------------- rows wins

  r = await call('/api/catalog/bulk', 'POST',
    { rows: [{ partNumber: typed }], partNumbers: ['ZZ-NOT-A-REAL-NUMBER'] }, admin);
  expect('rows wins when both shapes are sent', rowOf(r)?.found, true);
} finally {
  for (const probe of made) {
    await call(`/api/admin/products/${probe.id}`, 'DELETE', undefined, admin);
  }

  const after = (await call('/api/admin/products', 'GET', undefined, admin)).body?.products?.length;

  if (before !== undefined && after !== before) {
    bad('the catalogue is the size it was', `${before} before, ${after} after`);
  } else {
    line('the probe parts were removed and the catalogue is the size it was');
  }
}

console.log(failures === 0 ? '\nall passed' : `\n${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
