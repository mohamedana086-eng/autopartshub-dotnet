// Every admin write, on both APIs, on rows this script creates.
//
// HOW TO RUN
// ----------
//   node tools/admin-writes.mjs
//
// with the .NET API in Development on :5080 and the Node API on :3000.
//
// The rule: touch nothing that was already there. Each case creates its own
// row through whichever API is being tested, checks the answer against the
// other, and deletes it. Earlier in this project a test script deleted a real
// catalogue part because it picked "the first product in the list" instead of
// making one — the row counts at the end are here so that cannot happen
// quietly again.
const NODE = 'http://localhost:3000';
const NET = 'http://localhost:5080';

const login = async () => {
  const r = await fetch(`${NODE}/api/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: 'admin@autopartshub.com', password: 'admin123' }),
  });
  return r.headers.getSetCookie().map((c) => c.split(';')[0]).join('; ');
};
const cookie = await login();

const call = (base, path, method, body) =>
  fetch(base + path, {
    method,
    headers: { 'Content-Type': 'application/json', cookie },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  }).then(async (r) => ({ status: r.status, body: await r.json().catch(() => null) }));

const line = (label, detail) => console.log(`  ${label.padEnd(36)} ${detail}`);

let same = 0, total = 0;

/** Sends the same bad request to both and compares the refusal. */
const bothRefuse = async (label, path, method, body) => {
  total++;
  const [a, b] = await Promise.all([call(NODE, path, method, body), call(NET, path, method, body)]);
  const match = a.status === b.status && JSON.stringify(a.body) === JSON.stringify(b.body);
  if (match) same++;
  line(label, match
    ? `both ${a.status} ${JSON.stringify(a.body).slice(0, 62)}`
    : `node ${a.status} ${JSON.stringify(a.body).slice(0, 44)} | dotnet ${b.status} ${JSON.stringify(b.body).slice(0, 44)}`);
};

const before = {};
for (const k of ['suppliers', 'warehouses', 'outlets']) {
  before[k] = (await call(NODE, `/api/admin/${k}`, 'GET')).body[k].length;
}

console.log('refusals, sent to both:');
await bothRefuse('supplier: no name', '/api/admin/suppliers', 'POST', { code: 'X' });
await bothRefuse('supplier: no code', '/api/admin/suppliers', 'POST', { name: 'X' });
await bothRefuse('supplier: bad reliability', '/api/admin/suppliers', 'POST', { name: 'X', code: 'X', reliability: 'wonderful' });
await bothRefuse('supplier: rating 9', '/api/admin/suppliers', 'POST', { name: 'X', code: 'X', rating: 9 });
await bothRefuse('supplier: returns "maybe"', '/api/admin/suppliers', 'POST', { name: 'X', code: 'X', acceptsReturns: 'maybe' });
await bothRefuse('supplier: guarantee -1', '/api/admin/suppliers', 'POST', { name: 'X', code: 'X', guaranteeMonths: -1 });
await bothRefuse('supplier: unknown id', '/api/admin/suppliers/nope', 'PATCH', { rating: 3 });
await bothRefuse('supplier: delete unknown', '/api/admin/suppliers/nope', 'DELETE');
await bothRefuse('warehouse: no code', '/api/admin/warehouses', 'POST', { name: 'X' });
await bothRefuse('warehouse: no name', '/api/admin/warehouses', 'POST', { code: 'X' });
await bothRefuse('warehouse: priority 1.5', '/api/admin/warehouses', 'POST', { code: 'X', name: 'X', priority: 1.5 });
await bothRefuse('warehouse: unknown id', '/api/admin/warehouses/nope', 'PATCH', { code: 'X', name: 'X' });
await bothRefuse('warehouse: delete unknown', '/api/admin/warehouses/nope', 'DELETE');
await bothRefuse('outlet: no code', '/api/admin/outlets', 'POST', { name: 'X' });
await bothRefuse('outlet: no name', '/api/admin/outlets', 'POST', { code: 'X' });
await bothRefuse('outlet: unknown warehouse', '/api/admin/outlets', 'POST', { code: 'ZZQ', name: 'X', warehouseId: 'nope' });
await bothRefuse('outlet: unknown id', '/api/admin/outlets/nope', 'PATCH', { code: 'X', name: 'X' });
await bothRefuse('outlet: delete unknown', '/api/admin/outlets/nope', 'DELETE');

await bothRefuse('currency: code "EURO"', '/api/admin/currencies', 'POST', { code: 'EURO', name: 'X', symbol: 'X', rate: 1 });
await bothRefuse('currency: rate 0', '/api/admin/currencies', 'POST', { code: 'ZZZ', name: 'X', symbol: 'X', rate: 0 });
await bothRefuse('currency: unknown id', '/api/admin/currencies/nope', 'PATCH', { code: 'ZZZ', name: 'X', symbol: 'X', rate: 1 });
await bothRefuse('currency: delete unknown', '/api/admin/currencies/nope', 'DELETE');
await bothRefuse('tier: no name', '/api/admin/client-categories', 'POST', { markupPercent: 5 });
await bothRefuse('tier: markup "lots"', '/api/admin/client-categories', 'POST', { name: 'X', markupPercent: 'lots' });
await bothRefuse('tier: delete unknown', '/api/admin/client-categories/nope', 'DELETE');
await bothRefuse('rule: no label', '/api/admin/markup-rules', 'POST', { type: 'PERCENT', value: 1 });
await bothRefuse('rule: type HALF', '/api/admin/markup-rules', 'POST', { label: 'X', type: 'HALF', value: 1 });
await bothRefuse('rule: band ends below start', '/api/admin/markup-rules', 'POST', { label: 'X', value: 1, purchasePriceFrom: 50, purchasePriceTo: 10 });
await bothRefuse('rule: active "no"', '/api/admin/markup-rules/nope', 'PATCH', { active: 'no' });
await bothRefuse('rule: patch unknown', '/api/admin/markup-rules/nope', 'PATCH', { active: true });
await bothRefuse('rule: delete unknown', '/api/admin/markup-rules/nope', 'DELETE');
await bothRefuse('product: no part number', '/api/admin/products', 'POST', { name: 'X' });
await bothRefuse('product: no name', '/api/admin/products', 'POST', { partNumber: 'X' });
await bothRefuse('product: price -1', '/api/admin/products', 'POST', { partNumber: 'X', name: 'X', manufacturerId: 'a', vehicleSystemId: 'b', basePrice: -1 });
await bothRefuse('product: unknown manufacturer', '/api/admin/products', 'POST', { partNumber: 'X', name: 'X', manufacturerId: 'nope', vehicleSystemId: 'nope', basePrice: 1 });
await bothRefuse('product: patch unknown', '/api/admin/products/nope', 'PATCH', { partNumber: 'X', name: 'X', manufacturerId: 'a', vehicleSystemId: 'b', basePrice: 1 });
await bothRefuse('product: delete unknown', '/api/admin/products/nope', 'DELETE');
await bothRefuse('images: unknown product', '/api/admin/products/nope/images', 'PUT', { images: [] });
await bothRefuse('stock: unknown product', '/api/admin/products/nope/stock', 'PUT', { levels: [] });

// A supplier that is genuinely in use, and a warehouse that genuinely holds
// stock — both refusals name a real count, so both APIs have to agree on it.
const suppliers = (await call(NODE, '/api/admin/suppliers', 'GET')).body.suppliers;
const sourcing = suppliers.find((s) => s.productCount > 0);
if (sourcing) await bothRefuse('delete a supplier in use', `/api/admin/suppliers/${sourcing.id}`, 'DELETE');

const warehouses = (await call(NODE, '/api/admin/warehouses', 'GET')).body.warehouses;
const stocked = warehouses.find((w) => w.totalQuantity > 0);
if (stocked) await bothRefuse('delete a stocked warehouse', `/api/admin/warehouses/${stocked.id}`, 'DELETE');

// The base currency has to refuse three different ways, and each refusal
// protects something different: its rate is 1 by definition, deactivating it
// would leave every price denominated in something the catalogue no longer
// carries, and deleting it would do both.
const currencies = (await call(NODE, '/api/admin/currencies', 'GET')).body.currencies;
const base = currencies.find((c) => c.isBase);
if (base) {
  const same_ = { code: base.code, name: base.name, symbol: base.symbol };
  await bothRefuse('rescale the base currency', `/api/admin/currencies/${base.id}`, 'PATCH', { ...same_, rate: 2 });
  await bothRefuse('deactivate the base', `/api/admin/currencies/${base.id}`, 'PATCH', { ...same_, rate: 1, active: false });
  await bothRefuse('delete the base', `/api/admin/currencies/${base.id}`, 'DELETE');
}

const tiers = (await call(NODE, '/api/admin/client-categories', 'GET')).body.categories;
const peopled = tiers.find((t) => t.clientCount > 0);
if (peopled) await bothRefuse('delete a tier in use', `/api/admin/client-categories/${peopled.id}`, 'DELETE');

// A part that is genuinely on an order line, found through the orders API so
// this cannot fall back to deleting a real part that merely looks handy.
const onOrder = (await call(NODE, '/api/admin/orders', 'GET')).body.orders.flatMap((o) => o.lines)[0];
if (onOrder) await bothRefuse('delete a part on an order', `/api/admin/products/${onOrder.productId}`, 'DELETE');

console.log(`\n${same}/${total} refusals identical\n`);

// --- writes that succeed, one row per API, both removed -----------------
console.log('round trips (each API writes its own row, then deletes it):');

const roundTrip = async (base, label, path, create, edit) => {
  const made = await call(base, path, 'POST', create);
  if (made.status !== 201) { line(`${label} create`, `FAILED ${made.status} ${JSON.stringify(made.body)}`); return null; }
  const key = Object.keys(made.body)[0];
  const row = made.body[key];
  line(`${label} create`, `201 ${JSON.stringify(row).slice(0, 96)}`);

  const patched = await call(base, `${path}/${row.id}`, 'PATCH', edit);
  line(`${label} edit`, `${patched.status} ${JSON.stringify(patched.body[key] ?? patched.body).slice(0, 96)}`);

  const gone = await call(base, `${path}/${row.id}`, 'DELETE');
  line(`${label} delete`, `${gone.status} ${JSON.stringify(gone.body)}`);
  return { created: row, edited: patched.body[key] };
};

const shape = (row) => row && Object.fromEntries(
  Object.entries(row).filter(([k]) => k !== 'id').map(([k, v]) => [k, v]));

for (const [label, path, create, edit] of [
  ['supplier', '/api/admin/suppliers',
    { name: 'ZZ Probe Supply', code: 'ZZPR', reliability: 'reliable', rating: 4, acceptsReturns: true, country: 'Nowhere', guaranteeMonths: 12, defaultStockDays: 3 },
    { name: 'ZZ Probe Renamed', code: 'ZZPR', reliability: 'official', rating: 5 }],
  ['warehouse', '/api/admin/warehouses',
    { code: 'zzw', name: 'Probe depot', city: 'Nowhere', priority: 7 },
    { code: 'ZZW', name: 'Probe depot renamed', active: false, priority: 0 }],
  ['outlet', '/api/admin/outlets',
    { code: 'zzo', name: 'Probe counter', city: 'Nowhere', phone: '000' },
    { code: 'ZZO', name: 'Probe counter renamed', active: false }],
]) {
  const a = await roundTrip(NODE, `node ${label}`, path, create, edit);
  const b = await roundTrip(NET, `dotnet ${label}`, path, create, edit);
  total++;
  const match = JSON.stringify(shape(a?.created)) === JSON.stringify(shape(b?.created))
             && JSON.stringify(shape(a?.edited)) === JSON.stringify(shape(b?.edited));
  if (match) same++;
  line(`${label}: both APIs agree`, match ? 'yes' : 'NO');
  console.log('');
}

// --- nothing left behind -----------------------------------------------
console.log('row counts before -> after:');
let clean = true;
for (const k of ['suppliers', 'warehouses', 'outlets']) {
  const now = (await call(NODE, `/api/admin/${k}`, 'GET')).body[k].length;
  if (now !== before[k]) clean = false;
  console.log(`  ${k.padEnd(12)} ${before[k]} -> ${now}  ${now === before[k] ? 'ok' : 'LEFTOVER'}`);
}

console.log(`\n${same}/${total} identical${clean ? '' : '  — AND SOMETHING WAS LEFT BEHIND'}`);
if (!clean || same !== total) process.exitCode = 1;
