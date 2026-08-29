// Opening an account, and moving an order along its statuses.
//
// HOW TO RUN
// ----------
//   node tools/account-order.mjs
//
// with the .NET API in Development on :5080 and the Node API on :3000.
//
// These are the two writes with a consequence outside their own row. A
// registration hashes a password and hands back a session cookie, so what is
// checked is not only the response but that the account it opened can sign in
// on the OTHER API. A status change moves stock: shipping draws the units off
// the shelf and reversing it puts them back, and an order shown as shipped
// whose stock was never drawn down is the discrepancy a warehouse finds at the
// next count and cannot explain.
//
// Both accounts and both orders are created here and removed here, on a part
// this script owns.
import { installCsrf } from './csrf.mjs';

const NODE = 'http://localhost:3000';
const NET = 'http://localhost:5080';

// Both APIs refuse a write without a matching cross-site token. A browser
// gets that pairing for free; this makes every fetch below carry it too.
await installCsrf(NODE, NET);

const login = async (base, creds) => {
  const r = await fetch(`${base}/api/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(creds),
  });
  return {
    status: r.status,
    cookie: r.headers.getSetCookie().map((c) => c.split(';')[0]).join('; '),
    body: await r.json(),
  };
};

const admin = (await login(NODE, { email: 'admin@autopartshub.com', password: 'admin123' })).cookie;

const call = (base, path, method, body, cookie) =>
  fetch(base + path, {
    method,
    headers: { 'Content-Type': 'application/json', ...(cookie ? { cookie } : {}) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  }).then(async (r) => ({
    status: r.status,
    cookie: r.headers.getSetCookie().map((c) => c.split(';')[0]).join('; '),
    body: await r.json().catch(() => null),
  }));

const line = (label, detail) => console.log(`  ${label.padEnd(34)} ${detail}`);

let same = 0, total = 0;

const both = async (label, path, method, body, cookie) => {
  total++;
  const [a, b] = await Promise.all([
    call(NODE, path, method, body, cookie), call(NET, path, method, body, cookie),
  ]);
  const match = a.status === b.status && JSON.stringify(a.body) === JSON.stringify(b.body);
  if (match) same++;
  line(label, match
    ? `both ${a.status} ${JSON.stringify(a.body).slice(0, 60)}`
    : `node ${a.status} ${JSON.stringify(a.body).slice(0, 42)} | dotnet ${b.status} ${JSON.stringify(b.body).slice(0, 42)}`);
  return [a, b];
};

const clientsBefore = (await call(NODE, '/api/admin/clients', 'GET', undefined, admin)).body.clients.length;

/* --------------------------------------------------------- registration --- */

console.log('registrations that are refused:');
await both('nothing at all', '/api/auth/register', 'POST', {});
await both('no name', '/api/auth/register', 'POST', { email: 'zz@probe.invalid', password: 'aaaaaa' });
await both('no email', '/api/auth/register', 'POST', { name: 'ZZ', password: 'aaaaaa' });
await both('no password', '/api/auth/register', 'POST', { name: 'ZZ', email: 'zz@probe.invalid' });
await both('a five character password', '/api/auth/register', 'POST',
  { name: 'ZZ', email: 'zz@probe.invalid', password: 'aaaaa' });
// Self-registration cannot mint an admin, whatever the body says. Nor a
// salesperson, which would come with other people's customers attached.
await both('asking for ADMIN', '/api/auth/register', 'POST',
  { name: 'ZZ', email: 'zz@probe.invalid', password: 'aaaaaa', role: 'ADMIN' });
await both('asking for SALES', '/api/auth/register', 'POST',
  { name: 'ZZ', email: 'zz@probe.invalid', password: 'aaaaaa', role: 'SALES' });
await both('an address already taken', '/api/auth/register', 'POST',
  { name: 'ZZ', email: 'admin@autopartshub.com', password: 'aaaaaa' });

console.log('\none real account from each API:');

// Distinct addresses, because the second registration would otherwise be
// refused by the first — which is the taken-address case, already covered.
const opened = [];
for (const [base, label, email] of [
  [NODE, 'node  ', 'zz-probe-node@probe.invalid'],
  [NET, 'dotnet', 'zz-probe-dotnet@probe.invalid'],
]) {
  const r = await call(base, '/api/auth/register', 'POST',
    { name: 'ZZ Probe', email: email.toUpperCase(), password: 'probe-password', role: 'B2B', city: ' Nowhere ' });
  line(`${label} register`, `${r.status} ${JSON.stringify(r.body).slice(0, 84)}`);
  opened.push({ base, label, email, ...r });
}

// The response shape, with the id and the address they differ by removed.
total++;
const scrub = (v) => JSON.parse(JSON.stringify(v ?? null, (k, x) =>
  k === 'id' || k === 'email' ? undefined : x));
const openMatch = opened[0].status === opened[1].status
  && JSON.stringify(scrub(opened[0].body)) === JSON.stringify(scrub(opened[1].body));
if (openMatch) same++;
line('register: both APIs agree', openMatch ? 'yes' : 'NO');

// Both were registered with the address in capitals. It is stored lowercased
// either way, so the account can be found again by somebody who types it
// differently — and so the taken-address check above cannot be walked past by
// pressing shift.
total++;
const lowered = (await call(NODE, '/api/admin/clients', 'GET', undefined, admin)).body.clients
  .filter((c) => opened.some((o) => o.email === c.email));
if (lowered.length === 2) same++;
line('addresses stored lowercased', lowered.length === 2 ? 'yes' : `NO — found ${lowered.length} of 2`);

// The point of the exercise: a password hashed by one API is accepted by the
// other. Both directions, because the two use different bcrypt libraries and
// only one of them has ever written a hash into this table before today.
console.log('\ncross-signing in:');
for (const o of opened) {
  for (const [base, label] of [[NODE, 'node  '], [NET, 'dotnet']]) {
    total++;
    const r = await login(base, { email: o.email, password: 'probe-password' });
    const ok = r.status === 200 && r.body.user?.email === o.email;
    if (ok) same++;
    line(`${label} accepts the ${o.label.trim()} account`, ok ? 'yes' : `NO — ${r.status} ${JSON.stringify(r.body)}`);
  }
  // And that the wrong password is still refused, so the above is not just
  // an endpoint that lets anybody in.
  total++;
  const wrong = await login(NET, { email: o.email, password: 'probe-passworD' });
  if (wrong.status === 401) same++;
  line(`the wrong password is refused`, wrong.status === 401 ? 'yes' : `NO — ${wrong.status}`);
}

// New accounts start on the Retail tier however they applied — a B2B
// applicant is reviewed and moved later, not trusted on the way in.
const asStored = (await call(NODE, '/api/admin/clients', 'GET', undefined, admin)).body.clients
  .filter((c) => opened.some((o) => o.email === c.email));
for (const c of asStored) {
  total++;
  const right = c.role === 'B2B' && c.categoryName === 'Retail' && c.city === 'Nowhere';
  if (right) same++;
  line(`${c.email.split('@')[0].slice(-6)} stored as`, right
    ? `role ${c.role}, tier ${c.categoryName}, city ${c.city}`
    : `WRONG — role ${c.role}, tier ${c.categoryName}, city ${c.city}`);
}

console.log('\nclosing them again:');
for (const o of opened) {
  const c = asStored.find((x) => x.email === o.email);
  if (!c) { line(`${o.label} close`, 'nothing to close'); continue; }
  const gone = await call(NET, '/dev/forget-client', 'POST', { clientId: c.id });
  line(`${o.label} close`, `${gone.status} ${JSON.stringify(gone.body)}`);
}

/* --------------------------------------------------------- order status --- */

console.log('\nstatus changes that are refused:');
const orders = (await call(NODE, '/api/admin/orders', 'GET', undefined, admin)).body.orders;
const anyOrder = orders[0];

await both('not signed in', `/api/admin/orders/${anyOrder?.id ?? 'x'}`, 'PATCH', { status: 'shipped' });
await both('no status', `/api/admin/orders/${anyOrder?.id ?? 'x'}`, 'PATCH', {}, admin);
await both('a status nobody has', `/api/admin/orders/${anyOrder?.id ?? 'x'}`, 'PATCH', { status: 'lost' }, admin);
await both('an unknown order', '/api/admin/orders/nope', 'PATCH', { status: 'shipped' }, admin);

/* --------- one order per API, on a part this script owns ------------------ */

const refs = (await call(NODE, '/api/admin/products', 'GET', undefined, admin)).body;
const made = await call(NODE, '/api/admin/products', 'POST', {
  partNumber: 'ZZ-STATUS-PROBE', name: 'Status probe', basePrice: 10,
  manufacturerId: refs.manufacturers[0].id, vehicleSystemId: refs.systems[0].id,
  supplierId: refs.suppliers[0].id,
}, admin);

if (!made.body.product) {
  console.error('\ncould not create the probe part:', made.body.error);
  process.exit(1);
}
const productId = made.body.product.id;
const warehouse = refs.warehouses[0];
const retail = (await login(NODE, { email: 'walk-in@example.com', password: 'retail123' })).cookie;

const shelf = async () => {
  const r = await call(NODE, `/api/admin/products/${productId}/stock`, 'GET', undefined, admin);
  const l = r.body.levels.find((x) => x.warehouseId === warehouse.id) ?? {};
  return { quantity: l.quantity, reserved: l.reserved };
};

console.log('\nan order through its statuses, on each API:');
const placed = [];

try {
  await call(NODE, `/api/admin/products/${productId}/stock`, 'PUT',
    { levels: [{ warehouseId: warehouse.id, quantity: 20, reserved: 0 }] }, admin);

  // Three units off a shelf of twenty, walked the whole way.
  //
  // Accepting and picking change nothing on the shelf — the units were already
  // promised when the order was placed. Shipping is the moment they leave:
  // both columns come down, leaving seventeen with nothing reserved. Delivery
  // and payment are both on the gone side of that line, so neither moves
  // anything.
  //
  // The walk used to double back — shipped, then processing again — to prove
  // the reversal put the units back. It cannot any more: an order does not go
  // backwards, and the release it was really testing is now its own status.
  // See the cancellation walk below.
  const expected = [
    'accepted   20/3',
    'processing 20/3',
    'shipped    17/0',
    'delivered  17/0',
    'paid       17/0',
  ];

  /**
   * Puts an order's units back on the shelf, then forgets the order.
   *
   * `/dev/forget-order` releases a reserve, so it can only be handed an order
   * that is still holding one. After a walk that ends in `paid` — or in
   * `cancelled` — the reserve is already gone, and forgetting it would take
   * three units off a reservation of nothing and drift the count the other
   * way. So the shelf is put back to what a holding order implies first.
   */
  const restoreAndForget = async (label, id) => {
    await call(NODE, `/api/admin/products/${productId}/stock`, 'PUT',
      { levels: [{ warehouseId: warehouse.id, quantity: 20, reserved: 3 }] }, admin);
    const gone = await call(NET, '/dev/forget-order', 'POST', { orderId: id });
    line(`${label} order removed`, JSON.stringify(gone.body));
    return gone;
  };

  // Each API's order is placed, walked and removed before the next one
  // starts, so both see the same shelf rather than one seeing the other's
  // reserve.
  for (const [base, label] of [[NODE, 'node  '], [NET, 'dotnet']]) {
    const order = await call(base, '/api/orders', 'POST', { items: [{ productId, quantity: 3 }] }, retail);
    if (order.status !== 201) { line(`${label} place`, `FAILED ${JSON.stringify(order.body)}`); continue; }
    const id = order.body.order.id;
    placed.push({ base, label, id });

    const held = await shelf();
    line(`${label} placed`, `${order.body.order.reference} — shelf ${held.quantity}/${held.reserved}`);

    const steps = [];
    for (const status of ['accepted', 'processing', 'shipped', 'delivered', 'paid']) {
      const r = await call(base, `/api/admin/orders/${id}`, 'PATCH', { status }, admin);
      const s = await shelf();
      steps.push({ status, response: r.body, shelf: `${status.padEnd(10)} ${s.quantity}/${s.reserved}` });
      line(`${label} -> ${status.padEnd(12)}`, `${r.status} shelf ${s.quantity}/${s.reserved}`);
    }
    placed[placed.length - 1].steps = steps;

    total++;
    const right = JSON.stringify(steps.map((s) => s.shelf)) === JSON.stringify(expected);
    if (right) same++;
    line(`${label} shelf moved as expected`, right ? 'yes' : `NO — ${JSON.stringify(steps.map((s) => s.shelf))}`);

    await restoreAndForget(label, id);
    placed[placed.length - 1].removed = true;

    const back = await shelf();
    total++;
    if (back.quantity === 20 && back.reserved === 0) same++;
    line(`${label} shelf back to 20/0`, `${back.quantity}/${back.reserved}`);
  }

  /* ------------------------------------------- calling an order off --- */

  // The case the old two-state stock code could not express: the goods never
  // left, so `quantity` is untouched, but the promise against them has to end
  // or the units stay reserved for an order nobody will ever pick.
  console.log('\nan order called off, on each API:');
  for (const [base, label] of [[NODE, 'node  '], [NET, 'dotnet']]) {
    const order = await call(base, '/api/orders', 'POST', { items: [{ productId, quantity: 3 }] }, retail);
    if (order.status !== 201) { line(`${label} place`, `FAILED ${JSON.stringify(order.body)}`); continue; }
    const id = order.body.order.id;
    placed.push({ base, label: `${label} cancel`, id });

    const held = await shelf();
    line(`${label} placed`, `shelf ${held.quantity}/${held.reserved}`);

    // Without a reason it is refused, by both APIs, in the same sentence.
    await both(`${label} cancelled with no reason`,
      `/api/admin/orders/${id}`, 'PATCH', { status: 'cancelled' }, admin);

    const off = await call(base, `/api/admin/orders/${id}`, 'PATCH',
      { status: 'cancelled', reason: 'Probe: customer changed their mind' }, admin);
    const after = await shelf();
    line(`${label} -> cancelled`, `${off.status} shelf ${after.quantity}/${after.reserved}`);

    total++;
    // Twenty still on the shelf and nothing promised: the promise ended, the
    // stock did not move.
    const released = off.status === 200 && after.quantity === 20 && after.reserved === 0;
    if (released) same++;
    line(`${label} promise released, stock untouched`,
      released ? 'yes' : `NO — ${after.quantity}/${after.reserved}`);

    // And it is finished: nothing moves it afterwards.
    await both(`${label} cancelled order cannot be revived`,
      `/api/admin/orders/${id}`, 'PATCH', { status: 'processing' }, admin);

    await restoreAndForget(`${label} cancel`, id);
    placed[placed.length - 1].removed = true;
  }

  // The refusal nobody wants to meet, deliberately arranged. The order holds
  // three units; the shelf is edited to say none are reserved. Shipping would
  // now subtract three from a reserve of zero, the CHECK on StockLevel refuses
  // a negative count, and the status must not move either.
  console.log('\na shelf that disagrees with the order holding it:');
  const drift = await call(NODE, '/api/orders', 'POST', { items: [{ productId, quantity: 3 }] }, retail);
  if (drift.status === 201) {
    const id = drift.body.order.id;
    placed.push({ base: NODE, label: 'drift ', id });

    // Walked to `processing` BEFORE the shelf is edited, so that what the
    // attempt below meets is the stock refusing it rather than the lifecycle:
    // an order cannot go straight from placed to shipped any more, and a
    // refusal for the wrong reason would prove nothing about the CHECK.
    for (const status of ['accepted', 'processing']) {
      await call(NODE, `/api/admin/orders/${id}`, 'PATCH', { status }, admin);
    }

    await call(NODE, `/api/admin/products/${productId}/stock`, 'PUT',
      { levels: [{ warehouseId: warehouse.id, quantity: 20, reserved: 0 }] }, admin);
    line('shelf edited to', JSON.stringify(await shelf()));

    await both('shipping it is refused', `/api/admin/orders/${id}`, 'PATCH', { status: 'shipped' }, admin);

    const stuck = (await call(NODE, '/api/admin/orders', 'GET', undefined, admin))
      .body.orders.find((o) => o.id === id);
    total++;
    const held = stuck?.status === 'processing';
    if (held) same++;
    line('the status did not move', held ? `still ${stuck?.status}` : `NO — now ${stuck?.status}`);

    // Repair it the way the reconciliation would, so the clean-up below
    // releases a reserve that exists.
    await call(NODE, `/api/admin/products/${productId}/stock`, 'PUT',
      { levels: [{ warehouseId: warehouse.id, quantity: 20, reserved: 3 }] }, admin);
    line('shelf repaired to', JSON.stringify(await shelf()));
  }

  total++;
  const walked = placed.filter((p) => p.steps);
  const strip = (p) => p.steps.map((s) => [s.shelf, s.response.status]);
  const stepsMatch = walked.length === 2
    && JSON.stringify(strip(walked[0])) === JSON.stringify(strip(walked[1]));
  if (stepsMatch) same++;
  line('statuses: both APIs agree', stepsMatch ? 'yes' : 'NO');
} finally {
  console.log('\nclean-up:');
  for (const p of placed.filter((x) => !x.removed)) {
    // The shelf is put back to what a holding order implies rather than the
    // order being walked back to `processing`: an order does not go backwards
    // any more, and this loop also has to cope with one left mid-walk by a
    // failure, whose status could be anything.
    await call(NODE, `/api/admin/products/${productId}/stock`, 'PUT',
      { levels: [{ warehouseId: warehouse.id, quantity: 20, reserved: 3 }] }, admin);
    const gone = await call(NET, '/dev/forget-order', 'POST', { orderId: p.id });
    line(`${p.label} order removed`, JSON.stringify(gone.body));
  }
  await call(NODE, `/api/admin/products/${productId}/stock`, 'PUT', { levels: [] }, admin);
  const gone = await call(NODE, `/api/admin/products/${productId}`, 'DELETE', undefined, admin);
  line('probe part removed', `${gone.status} ${JSON.stringify(gone.body)}`);
}

/* ------------------------------------------------------ nothing left over --- */

console.log('\nrow counts before -> after:');
const clientsAfter = (await call(NODE, '/api/admin/clients', 'GET', undefined, admin)).body.clients.length;
const clean = clientsAfter === clientsBefore;
console.log(`  clients      ${clientsBefore} -> ${clientsAfter}  ${clean ? 'ok' : 'LEFTOVER'}`);

console.log(`\n${same}/${total} identical${clean ? '' : '  — AND SOMETHING WAS LEFT BEHIND'}`);
if (!clean || same !== total) process.exitCode = 1;
