// Placing an order: the refusals on both APIs, then one real order end to end.
//
// HOW TO RUN
// ----------
//   node tools/order-post.mjs
//
// with the .NET API in Development on :5080 and the Node API on :3000.
//
// The refusals write nothing, so both APIs can be asked the same bad requests
// and their answers compared directly. The one order that does get placed is
// for a part this script creates, and it is removed afterwards along with its
// lines and allocations — an order is a record of a sale, and leaving invented
// ones in a live database is not a thing to do casually.
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
  return r.headers.getSetCookie().map((c) => c.split(';')[0]).join('; ');
};

const admin = await login(NODE, { email: 'admin@autopartshub.com', password: 'admin123' });
const retail = await login(NODE, { email: 'walk-in@example.com', password: 'retail123' });

const post = (base, cookie, body) =>
  fetch(`${base}/api/orders`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(cookie ? { cookie } : {}) },
    body: JSON.stringify(body),
  }).then(async (r) => ({ status: r.status, body: await r.json() }));

const line = (label, detail) => console.log(`  ${label.padEnd(34)} ${detail}`);

console.log('refusals (nothing is written):');
let same = 0, total = 0;

for (const [label, cookie, body] of [
  ['not signed in', null, { items: [{ productId: 'x', quantity: 1 }] }],
  ['no items', retail, {}],
  ['empty list', retail, { items: [] }],
  ['too many lines', retail, { items: Array.from({ length: 201 }, () => ({ productId: 'x', quantity: 1 })) }],
  ['line with no product', retail, { items: [{ quantity: 1 }] }],
  ['quantity zero', retail, { items: [{ productId: 'x', quantity: 0 }] }],
  ['quantity 1000', retail, { items: [{ productId: 'x', quantity: 1000 }] }],
  ['quantity not a number', retail, { items: [{ productId: 'x', quantity: 'two' }] }],
  ['unknown product', retail, { items: [{ productId: 'no-such-product', quantity: 1 }] }],
]) {
  total++;
  const [a, b] = await Promise.all([post(NODE, cookie, body), post(NET, cookie, body)]);
  const match = a.status === b.status && JSON.stringify(a.body) === JSON.stringify(b.body);
  if (match) same++;
  line(label, match
    ? `both ${a.status} ${JSON.stringify(a.body).slice(0, 66)}`
    : `node ${a.status} ${JSON.stringify(a.body).slice(0, 50)} | dotnet ${b.status} ${JSON.stringify(b.body).slice(0, 50)}`);
}

console.log(`\n${same}/${total} refusals identical`);

// --- one real order, on a part this script owns ------------------------
const api = (path, method, body) =>
  fetch(NODE + path, {
    method, headers: { 'Content-Type': 'application/json', cookie: admin },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });

const refs = await (await api('/api/admin/products', 'GET')).json();
const made = await (await api('/api/admin/products', 'POST', {
  partNumber: 'ZZ-ORDER-PROBE', name: 'Order probe', basePrice: 10,
  manufacturerId: refs.manufacturers[0].id, vehicleSystemId: refs.systems[0].id,
  supplierId: refs.suppliers[0].id,
})).json();

if (!made.product) { console.error('\ncould not create the probe part:', made.error); process.exit(1); }
const productId = made.product.id;
const warehouse = refs.warehouses[0];

console.log(`\none real order, on ${made.product.partNumber}:`);

try {
  await api(`/api/admin/products/${productId}/stock`, 'PUT', {
    levels: [{ warehouseId: warehouse.id, quantity: 5, reserved: 0 }],
  });

  // Out of stock first: asking for more than exists writes nothing, and both
  // APIs should refuse it the same way.
  const [overA, overB] = await Promise.all([
    post(NODE, retail, { items: [{ productId, quantity: 6 }] }),
    post(NET, retail, { items: [{ productId, quantity: 6 }] }),
  ]);
  line('asking for 6 of 5', overA.status === overB.status && JSON.stringify(overA.body) === JSON.stringify(overB.body)
    ? `both ${overA.status} ${overA.body.error}`
    : `node ${JSON.stringify(overA.body)} | dotnet ${JSON.stringify(overB.body)}`);

  // Then one that succeeds, placed on the .NET side.
  const placed = await post(NET, retail, { items: [{ productId, quantity: 2 }] });
  const o = placed.body.order;
  line('placing 2', placed.status === 201
    ? `201 ${o.reference} total ${o.total} status ${o.status}`
    : `${placed.status} ${JSON.stringify(placed.body)}`);

  const after = await (await api(`/api/admin/products/${productId}/stock`, 'GET')).json();
  const level = after.levels[0];
  line('shelf after', `quantity ${level.quantity}, reserved ${level.reserved}, available ${level.available}`);
  line('reserved 2 as expected', level.reserved === 2 ? 'yes' : `NO — got ${level.reserved}`);

  // And that it reads back the same from both APIs.
  const [ordersA, ordersB] = await Promise.all([
    fetch(`${NODE}/api/orders`, { headers: { cookie: retail } }).then((r) => r.json()),
    fetch(`${NET}/api/orders`, { headers: { cookie: retail } }).then((r) => r.json()),
  ]);
  line('both APIs read it identically',
    JSON.stringify(ordersA) === JSON.stringify(ordersB) ? 'yes' : 'NO');
  line('the new order is in it',
    ordersA.orders.some((x) => x.reference === o.reference) ? 'yes' : 'NO');

  // --- clean up ------------------------------------------------------
  // The order has to go before the part can: OrderItem references Product
  // and the delete is refused while it does. Allocations first, then lines,
  // then the order — each is the child of the next.
  //
  // Releasing the reserve is not optional. Deleting the allocation rows
  // without lowering StockLevel.reserved leaves the shelf promising units to
  // an order that no longer exists, which is exactly the drift db:reconcile
  // was written to find.
  console.log('\nclean-up:');
  const cleanup = await fetch(`${NET}/dev/forget-order`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ orderId: o.id }),
  }).then((r) => r.json());
  line('order removed, reserve released', JSON.stringify(cleanup));

  const released = await (await api(`/api/admin/products/${productId}/stock`, 'GET')).json();
  line('shelf back to', `quantity ${released.levels[0]?.quantity}, reserved ${released.levels[0]?.reserved}`);
} finally {
  await api(`/api/admin/products/${productId}/stock`, 'PUT', { levels: [] });
  const gone = await api(`/api/admin/products/${productId}`, 'DELETE');
  console.log(`probe part removed: ${gone.status} ${gone.status === 200 ? '' : await gone.text()}`);
}
