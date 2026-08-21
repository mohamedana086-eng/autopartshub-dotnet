// Can two orders be sold the same last unit?
//
// HOW TO RUN
// ----------
//   node tools/stock-race.mjs
//
// with the .NET API in Development on :5080 and the Node API on :3000. The
// Node API is used only to create and remove the probe part through the admin
// endpoints, so this touches nothing it did not make.
//
// One reservation at a time proves arithmetic. The property worth testing is
// that two requests for the same last unit cannot both be told yes, and the
// only way to ask is to send them together and hold the transactions open
// while they overlap.
const NODE = 'http://localhost:3000';
const NET = 'http://localhost:5080';

const login = async () => {
  const r = await fetch(`${NODE}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: 'admin@autopartshub.com', password: 'admin123' }),
  });
  return r.headers.getSetCookie().map((c) => c.split(';')[0]).join('; ');
};

const cookie = await login();
const admin = (path, method, body) =>
  fetch(NODE + path, {
    method,
    headers: { 'Content-Type': 'application/json', cookie },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });

const reserve = (body) =>
  fetch(`${NET}/dev/reserve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).then((r) => r.json());

const line = (label, detail) => console.log(`  ${label.padEnd(38)} ${detail}`);

// --- a part this script owns, with a known count -------------------------
const refs = await (await admin('/api/admin/products', 'GET')).json();
const made = await (await admin('/api/admin/products', 'POST', {
  partNumber: 'ZZ-STOCK-RACE',
  name: 'Stock race probe',
  basePrice: 1,
  manufacturerId: refs.manufacturers[0].id,
  vehicleSystemId: refs.systems[0].id,
  supplierId: refs.suppliers[0].id,
})).json();

if (!made.product) {
  console.error('could not create the probe part:', made.error);
  process.exit(1);
}

const id = made.product.id;
const warehouse = refs.warehouses[0];
const STOCK = 3;

try {
  await admin(`/api/admin/products/${id}/stock`, 'PUT', {
    levels: [{ warehouseId: warehouse.id, quantity: STOCK, reserved: 0 }],
  });
  console.log(`probe part ${made.product.partNumber}: ${STOCK} units at ${warehouse.name ?? warehouse.id}\n`);

  // --- one at a time, to show the arithmetic --------------------------
  console.log('one reservation at a time (rolled back each time):');
  line(`asking for ${STOCK}`, JSON.stringify(await reserve({ productId: id, quantity: STOCK })).slice(0, 120));
  line(`asking for ${STOCK + 1}`, JSON.stringify((await reserve({ productId: id, quantity: STOCK + 1 })).shortfall));
  line('asking for 1', JSON.stringify((await reserve({ productId: id, quantity: 1 })).allocations));

  // --- the race --------------------------------------------------------
  console.log('\ntwo requests for all of it, at once, both committing:');
  const [a, b] = await Promise.all([
    reserve({ productId: id, quantity: STOCK, commit: true, holdMs: 600 }),
    reserve({ productId: id, quantity: STOCK, commit: true, holdMs: 600 }),
  ]);

  line('first', a.ok ? `held ${a.allocations.map((x) => x.quantity).join('+')}` : `refused: ${JSON.stringify(a.shortfall)}`);
  line('second', b.ok ? `held ${b.allocations.map((x) => x.quantity).join('+')}` : `refused: ${JSON.stringify(b.shortfall)}`);

  const winners = [a, b].filter((r) => r.ok).length;
  line('exactly one won', winners === 1 ? 'yes' : `NO — ${winners} succeeded`);

  const after = await (await admin(`/api/admin/products/${id}/stock`, 'GET')).json();
  const level = after.levels[0];
  line('shelf afterwards', `quantity ${level.quantity}, reserved ${level.reserved}, available ${level.available}`);
  line('reserved matches one sale', level.reserved === STOCK ? 'yes' : `NO — expected ${STOCK}`);

  // --- the guard --------------------------------------------------------
  console.log('\nreserving with no transaction open:');
  const unsafe = await fetch(`${NET}/dev/reserve-unsafe`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ productId: id, quantity: 1 }),
  }).then((r) => r.json());
  line('refused', unsafe.guarded ? 'yes' : 'NO — it reserved without a lock');
} finally {
  await admin(`/api/admin/products/${id}/stock`, 'PUT', { levels: [] });
  const gone = await admin(`/api/admin/products/${id}`, 'DELETE');
  console.log(`\nprobe part removed: ${gone.status}`);
}
