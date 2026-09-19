// A supplier's own lines, and what they may say about them.
//
// HOW TO RUN
// ----------
//   node tools/supplier-products.mjs
//
// with the .NET API in Development on :5080, signed-in-able as the seeded
// admin. One API: GET/PATCH /api/supplier/products is the shape the backlog
// specifies and the Node API does not serve it yet.
//
// WHY A HARNESS
// -------------
// Everything worth checking here is about who is asking. Two suppliers, a
// part each, and the question is whether either can see or touch the other's
// line — which needs two accounts, two sessions, and a server holding the
// distinction. The portal's whole doctrine is what it does NOT send, and that
// is not a thing a unit test can be pointed at.
//
// It makes its own two suppliers, its own two parts and its own offers, and
// takes the parts away afterwards. The supplier accounts stay, named
// @probe.invalid, as account-order.mjs leaves its own.

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

  return {
    status: res.status,
    body: parsed,
    cookies: res.headers.getSetCookie().map((c) => c.split(';')[0]).join('; '),
  };
}

const adminIn = await call('/api/auth/login', 'POST',
  { email: 'admin@autopartshub.com', password: 'admin123' });

if (adminIn.status !== 200) {
  console.error(`could not sign in as the seeded admin: ${adminIn.status}`);
  process.exit(1);
}

const admin = adminIn.cookies;
const stamp = Date.now();

// --------------------------------------------------------------- two suppliers

async function supplier(which) {
  const email = `zz-supplier-${which}-${stamp}@probe.invalid`;

  const made = await call('/api/auth/register-supplier', 'POST', {
    company: `ZZ Probe Supplier ${which} ${stamp}`,
    code: `ZZP${which}${String(stamp).slice(-5)}`,
    email,
    password: 'probe-password',
  });

  if (made.status !== 201 && made.status !== 200) {
    console.error(`could not register supplier ${which}:`, JSON.stringify(made.body));
    process.exit(1);
  }

  // A sign-up waits for an admin. Approving is what turns the account into one
  // the portal will answer — and going through the real route rather than SQL
  // is the point: it is the same path a real supplier takes.
  const waiting = await call('/api/admin/suppliers/waiting', 'GET', undefined, admin);
  const mine = (waiting.body?.suppliers ?? []).find((s) => s.email === email || s.contactEmail === email);
  const supplierId = mine?.id ?? made.body?.supplier?.id;

  if (!supplierId) {
    console.error(`could not find supplier ${which} to approve:`, JSON.stringify(waiting.body));
    process.exit(1);
  }

  const approved = await call(`/api/admin/suppliers/${supplierId}/approval`, 'PATCH',
    { active: true }, admin);

  if (approved.status !== 200) {
    console.error(`could not approve supplier ${which}:`, JSON.stringify(approved.body));
    process.exit(1);
  }

  const signedIn = await call('/api/auth/login', 'POST',
    { email, password: 'probe-password' });

  if (signedIn.status !== 200) {
    console.error(`could not sign in as supplier ${which}:`, JSON.stringify(signedIn.body));
    process.exit(1);
  }

  return { id: supplierId, email, cookie: signedIn.cookies };
}

const one = await supplier('one');
const two = await supplier('two');

console.log(`suppliers ${one.id} and ${two.id}\n`);

// ------------------------------------------------------------ a part each

const references = await call('/api/admin/products', 'GET', undefined, admin);
const before = references.body?.products?.length;
const brand = references.body?.manufacturers?.[0]?.id;
const system = references.body?.systems?.[0]?.id;

const parts = [];
for (const [at, owner] of [one, two].entries()) {
  const made = await call('/api/admin/products', 'POST', {
    partNumber: `ZZ-SUP-${at}-${stamp}`,
    name: `Supplier portal probe ${at}`,
    basePrice: 20 + at,
    manufacturerId: brand,
    vehicleSystemId: system,
  }, admin);

  const id = made.body?.product?.id;
  if (!id) {
    console.error('could not create a probe part:', JSON.stringify(made.body));
    for (const p of parts) await call(`/api/admin/products/${p.id}`, 'DELETE', undefined, admin);
    process.exit(1);
  }

  // The offer is what the portal is about, and the admin editor is what makes
  // one. It takes the whole set for a part, which here is one.
  const offered = await call(`/api/admin/products/${id}/offers`, 'PUT', {
    offers: [{ supplierId: owner.id, purchasePrice: 10 + at, stockDays: 3 }],
  }, admin);

  if (offered.status !== 200) {
    console.error('could not make the offer:', offered.status, JSON.stringify(offered.body));
    for (const p of [...parts, { id }]) await call(`/api/admin/products/${p.id}`, 'DELETE', undefined, admin);
    process.exit(1);
  }

  parts.push({ id, owner });
}

const mineOf = (res) => res.body?.products ?? [];

try {
  // ------------------------------------------------------- only their own

  let r = await call('/api/supplier/products', 'GET', undefined, one.cookie);
  expect('a supplier sees their own line', r.status, 200);
  expect('  and exactly one of them', mineOf(r).length, 1);
  expect('  and it is theirs', mineOf(r)[0]?.productId, parts[0].id);

  r = await call('/api/supplier/products', 'GET', undefined, two.cookie);
  expect("and not the other supplier's", mineOf(r)[0]?.productId, parts[1].id);

  // The competitive fact the portal withholds on purpose.
  const shown = Object.keys(mineOf(r)[0] ?? {});
  if (shown.some((k) => /best|winning|cheapest|competitor/i.test(k))) {
    bad('no line says whether they are being outbid', shown.join(', '));
  } else {
    line('no line says whether they are being outbid');
  }

  // ---------------------------------------------------------- the refusals

  r = await call(`/api/supplier/products/${parts[0].id}`, 'PATCH',
    { stockDays: 9 }, two.cookie);
  expect("a supplier cannot touch another's line", r.status, 404);

  r = await call(`/api/supplier/products/${parts[0].id}`, 'PATCH', { stockDays: 9 }, admin);
  expect('an admin is not a supplier here', r.status, 403);

  r = await call(`/api/supplier/products/${parts[0].id}`, 'PATCH', { stockDays: 9 });
  expect('an anonymous caller is refused', r.status, 401);

  // The one they may not set, refused by name rather than dropped.
  r = await call(`/api/supplier/products/${parts[0].id}`, 'PATCH',
    { purchasePrice: 1 }, one.cookie);
  expect('a price change is refused', r.status, 400);
  if (typeof r.body?.error === 'string' && /price list/i.test(r.body.error)) {
    line('  and says where a price change goes');
  } else {
    bad('  and says where a price change goes', JSON.stringify(r.body));
  }

  r = await call(`/api/supplier/products/${parts[0].id}`, 'PATCH', {}, one.cookie);
  expect('a PATCH that changes nothing is refused', r.status, 400);

  r = await call(`/api/supplier/products/${parts[0].id}`, 'PATCH',
    { stockDays: 400 }, one.cookie);
  expect('a lead time past a year is refused', r.status, 400);

  // ----------------------------------------------------------- the edits

  r = await call(`/api/supplier/products/${parts[0].id}`, 'PATCH',
    { stockDays: 7, supplierPartNumber: 'THEIR-NUMBER-1', active: true }, one.cookie);
  expect('a supplier states their lead time', r.body?.product?.stockDays, 7);
  expect('  and their own part number', r.body?.product?.supplierPartNumber, 'THEIR-NUMBER-1');

  // The property a CASE-per-field exists for: naming one does not blank the
  // others.
  r = await call(`/api/supplier/products/${parts[0].id}`, 'PATCH',
    { active: false }, one.cookie);
  expect('withdrawing the line takes', r.body?.product?.active, false);
  expect('  and leaves the lead time alone', r.body?.product?.stockDays, 7);
  expect('  and the part number alone', r.body?.product?.supplierPartNumber, 'THEIR-NUMBER-1');

  // And the price is where it was, because nothing here can move it.
  expect('  and the price is untouched', r.body?.product?.purchasePrice, 10);

  // An empty string clears the number rather than storing a blank.
  r = await call(`/api/supplier/products/${parts[0].id}`, 'PATCH',
    { supplierPartNumber: '  ' }, one.cookie);
  expect('an empty part number clears it', r.body?.product?.supplierPartNumber, null);

  // A withdrawn line is still theirs to see and to bring back.
  r = await call('/api/supplier/products', 'GET', undefined, one.cookie);
  expect('a withdrawn line stays on their list', mineOf(r).length, 1);
  expect('  and reads as withdrawn', mineOf(r)[0]?.active, false);
} finally {
  for (const p of parts) await call(`/api/admin/products/${p.id}`, 'DELETE', undefined, admin);

  const after = (await call('/api/admin/products', 'GET', undefined, admin)).body?.products?.length;

  if (before !== undefined && after !== before) {
    bad('the catalogue is the size it was', `${before} before, ${after} after`);
  } else {
    line('the probe parts were removed and the catalogue is the size it was');
  }

  console.log(`\n  --    the supplier accounts stay: ${one.email}, ${two.email}`);
}

console.log(failures === 0 ? '\nall passed' : `\n${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
