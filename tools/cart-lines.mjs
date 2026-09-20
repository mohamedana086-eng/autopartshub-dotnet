// The basket, a line at a time — and whether two tabs can lose an add.
//
// HOW TO RUN
// ----------
//   node tools/cart-lines.mjs
//
// with the .NET API in Development on :5080, signed-in-able as the seeded
// admin. ONE API, unlike most of the harnesses beside it: POST/PATCH/DELETE
// /api/cart/lines is the shape the backlog specifies and the Node API does not
// serve it yet, so there is nothing to compare against. What is being checked
// is that the new shape agrees with the whole-basket PUT about what a basket
// may hold, and that it survives being asked twice at once.
//
// WHY A HARNESS AND NOT A TEST
// ----------------------------
// The interesting property is a race. Adding two to a line means reading what
// is there and writing what should be — and two tabs doing that together can
// each read one, each write two, and lose an add. The only way to ask is to
// send the requests together, which needs a running server and a real
// database, and neither belongs in a unit test.
//
// It makes its own part and its own account and removes the part afterwards,
// counting before and after. The account stays, named @probe.invalid, the same
// way account-order.mjs leaves its own — there is no endpoint that deletes one
// and inventing one for a test script would be the wrong way round.

import { installCsrf } from './csrf.mjs';

const NET = process.env.NET ?? 'http://localhost:5080';

await installCsrf(NET);

let failures = 0;

const line = (label, detail = '') => console.log(`  ok    ${label}${detail ? `  ${detail}` : ''}`);
const bad = (label, detail) => { failures++; console.log(`  FAIL  ${label}\n        ${detail}`); };

/** One request, with its cookies, returning status and parsed body. */
async function call(path, method = 'GET', body, cookie) {
  const res = await fetch(NET + path, {
    method,
    headers: {
      'Content-Type': 'application/json',
      ...(cookie ? { cookie } : {}),
    },
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

const expect = (label, got, want, detail) =>
  got === want ? line(label, detail) : bad(label, `expected ${want}, got ${got}`);

// ------------------------------------------------------------------ sign in

const signIn = await call('/api/auth/login', 'POST',
  { email: 'admin@autopartshub.com', password: 'admin123' });

if (signIn.status !== 200) {
  console.error(`could not sign in as the seeded admin: ${signIn.status}`);
  console.error(JSON.stringify(signIn.body));
  process.exit(1);
}

const admin = signIn.cookies;

// -------------------------------------------------------------- a probe part
//
// Made here rather than picked from the catalogue. A script that picks "the
// first product in the list" and then writes to it is how this project once
// deleted a real part.

const references = await call('/api/admin/products', 'GET', undefined, admin);
const before = references.body?.products?.length;

const first = (list) => (Array.isArray(list) && list.length > 0 ? list[0].id : null);

const made = await call('/api/admin/products', 'POST', {
  partNumber: `ZZ-CART-PROBE-${Date.now()}`,
  name: 'Cart line probe',
  basePrice: 10,
  manufacturerId: first(references.body?.manufacturers),
  vehicleSystemId: first(references.body?.systems),
}, admin);

const productId = made.body?.product?.id;

if (!productId) {
  console.error('could not create the probe part:', JSON.stringify(made.body));
  process.exit(1);
}

console.log(`probe part ${productId}\n`);

// --------------------------------------------------------------- the account

const email = `zz-cart-probe-${Date.now()}@probe.invalid`;

const registered = await call('/api/auth/register', 'POST',
  { name: 'ZZ Cart Probe', email, password: 'probe-password', role: 'B2B' }, admin);

if (registered.status !== 201) {
  console.error('could not register the probe account:', JSON.stringify(registered.body));
  await call(`/api/admin/products/${productId}`, 'DELETE', undefined, admin);
  process.exit(1);
}

const customer = registered.cookies;

const cart = (method, path, body) => call(`/api/cart${path}`, method, body, customer);
const quantityOf = (body, id) => body?.items?.find((i) => i.productId === id)?.quantity ?? 0;

try {
  // ------------------------------------------------------------- adding

  let r = await cart('POST', '/lines', { productId, quantity: 2 });
  expect('POST adds a line', r.status, 200, `x${quantityOf(r.body, productId)}`);

  r = await cart('POST', '/lines', { productId, quantity: 2 });
  expect('POST again adds to it', quantityOf(r.body, productId), 4);

  // ------------------------------------------------------------ setting

  r = await cart('PATCH', `/lines/${productId}`, { quantity: 6 });
  expect('PATCH sets the line', quantityOf(r.body, productId), 6);

  r = await cart('PATCH', '/lines/prd-not-a-real-part', { quantity: 1 });
  expect('PATCH on a line that is not there is refused', r.status, 404);

  // --------------------------------------------------- the packaging rule
  //
  // NOT CHECKED HERE, and the reason is worth writing down: no endpoint sets
  // `quantityPerPackage`. It is read by the cart, the line routes and the
  // order endpoint, and written only by an import — so a probe part made
  // through the API is always one-to-a-package, and a pack size cannot be put
  // on it to refuse.
  //
  // The rule itself is covered by PackagingTests, and the line routes reach it
  // through the same RefuseAsync the whole-basket PUT uses, which is the
  // property that matters: there is one place that decides, not two. What
  // cannot be shown from out here is that a part with a real pack size is
  // refused by the new shape as well as the old — that wants either an import
  // or a seeded part, and is worth doing on the day one exists.

  // ---------------------------------------------------------- refusals

  r = await cart('POST', '/lines', { productId, quantity: 0 });
  expect('a quantity of zero is refused', r.status, 400);

  r = await cart('POST', '/lines', { productId: 'prd-not-a-real-part', quantity: 2 });
  expect('a part that is not in the catalogue is refused', r.status, 409);

  // ---------------------------------------------------------- removing

  r = await cart('DELETE', `/lines/${productId}`);
  expect('DELETE removes the line', quantityOf(r.body, productId), 0);

  r = await cart('DELETE', `/lines/${productId}`);
  expect('DELETE again succeeds rather than 404s', r.status, 200);

  // ------------------------------------------------------------ the race
  //
  // The reason this file exists. Eight adds of one package, sent together.
  // Read-modify-write without a lock loses some of them, and loses a
  // different number each run — which is the shape of bug that gets closed as
  // "could not reproduce".

  const together = await Promise.all(
    Array.from({ length: 8 }, () => cart('POST', '/lines', { productId, quantity: 2 })));

  const refused = together.filter((x) => x.status !== 200);
  if (refused.length > 0) {
    bad('eight concurrent adds all answered', `${refused.length} did not: ${refused[0].status}`);
  } else {
    line('eight concurrent adds all answered');
  }

  r = await cart('GET', '');
  expect('and not one of them was lost', quantityOf(r.body, productId), 16);

  // -------------------------------------------------- the two shapes agree

  r = await call('/api/cart', 'PUT', { items: [{ productId, quantity: 4 }] }, customer);
  expect('the whole-basket PUT still works beside them', quantityOf(r.body, productId), 4);

  r = await cart('POST', '/lines', { productId, quantity: 2 });
  expect('and a line added after it builds on what PUT left', quantityOf(r.body, productId), 6);
} finally {
  // ------------------------------------------------------------- cleanup

  await cart('DELETE', `/lines/${productId}`);
  const removed = await call(`/api/admin/products/${productId}`, 'DELETE', undefined, admin);

  const after = (await call('/api/admin/products', 'GET', undefined, admin)).body?.products?.length;

  if (removed.status !== 200 && removed.status !== 204) {
    bad('the probe part was removed', `DELETE answered ${removed.status}`);
  } else if (before !== undefined && after !== before) {
    bad('the catalogue is the size it was', `${before} before, ${after} after`);
  } else {
    line('the probe part was removed and the catalogue is the size it was');
  }

  console.log(`\nthe probe account ${email} stays — there is no endpoint that deletes one.`);
}

console.log(failures === 0 ? '\nall passed' : `\n${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
