// Sends the same requests to both APIs and diffs the responses.
//
// HOW TO RUN
// ----------
//   node tools/compare.mjs            every group
//   node tools/compare.mjs product    one group, by name
//
// with the .NET API in Development on :5080 and the Node API on :3000.
//
// The test for a port is not that the new version looks right, it is that it
// is indistinguishable from the one already serving customers. Both read the
// same database, so the same request can be asked of each.
//
// Cases are signed in as the account named, because almost every response is
// priced for whoever is asking and the anonymous shape is only one of them.
const NODE = 'http://localhost:3000';
const NET = 'http://localhost:5080';

const ACCOUNTS = {
  anonymous: null,
  retail: { email: 'walk-in@example.com', password: 'retail123' },
  admin: { email: 'admin@autopartshub.com', password: 'admin123' },
};

const cookies = {};
for (const [name, creds] of Object.entries(ACCOUNTS)) {
  if (!creds) { cookies[name] = ''; continue; }
  const res = await fetch(`${NODE}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(creds),
  });
  // One cookie for both, which the interop test already proved they share.
  cookies[name] = res.ok
    ? res.headers.getSetCookie().map((c) => c.split(';')[0]).join('; ')
    : null;
}

/** Where the two responses first disagree, as a path. */
function firstDifference(a, b, path = '') {
  if (JSON.stringify(a) === JSON.stringify(b)) return null;

  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) return `${path}.length  node ${a.length}  dotnet ${b.length}`;
    for (let i = 0; i < a.length; i++) {
      const d = firstDifference(a[i], b[i], `${path}[${i}]`);
      if (d) return d;
    }
    return null;
  }

  if (a && b && typeof a === 'object' && typeof b === 'object') {
    const keysA = Object.keys(a), keysB = Object.keys(b);
    if (keysA.join() !== keysB.join()) {
      return `${path} keys differ\n      node   ${keysA.join(', ')}\n      dotnet ${keysB.join(', ')}`;
    }
    for (const k of keysA) {
      const d = firstDifference(a[k], b[k], `${path}.${k}`);
      if (d) return d;
    }
    return null;
  }

  return `${path}\n      node   ${JSON.stringify(a)}\n      dotnet ${JSON.stringify(b)}`;
}

const GROUPS = {
  reference: [
    ['anonymous', '/api/systems'],
    ['anonymous', '/api/suppliers'],
    ['anonymous', '/api/vehicles'],
    ['anonymous', '/api/vehicles/vin?vin=WBA3B1C50DF123456'],
    ['anonymous', '/api/vehicles/vin?vin=ZZZ3B1C50DF123456'],
    ['anonymous', '/api/vehicles/vin?vin=nope'],
  ],
  session: [
    ['anonymous', '/api/auth/session'],
    ['retail', '/api/auth/session'],
    ['admin', '/api/auth/session'],
  ],
  product: [],   // filled in below, once a real id is known
};

// Product ids are not guessable, so they come from the catalogue itself.
const search = await (await fetch(`${NODE}/api/catalog/search?q=`)).json();
const sample = search.products.slice(0, 4);
for (const p of sample) {
  for (const who of ['anonymous', 'retail', 'admin']) {
    GROUPS.product.push([who, `/api/catalog/products/${p.id}`]);
  }
}
GROUPS.product.push(['anonymous', '/api/catalog/products/no-such-id']);

GROUPS.search = [];
for (const qs of [
  "",
  "q=brake",
  "q=brembo",
  "q=bosch brake pad",
  "q=0986424815",
  "q=0 986 424 815",
  "q=P 06 020",
  "q=brak pd",
  "q=zzzznothing",
  "q=thermostat",
  "system=brake-system",
  "system=cooling-system&q=thermostat",
  "manufacturer=BOSCH",
  "manufacturer=bosch",
  "sort=price-asc",
  "sort=price-desc",
  "sort=delivery",
  "sort=nonsense",
  "minRating=4",
  "minRating=9",
  "reliability=official",
  "reliability=nope",
  "returns=true",
  "limit=3",
  "limit=0",
  "limit=9999",
  "minPrice=20&maxPrice=60",
  "minPrice=-5",
  "q=brake&matchIn=part-number",
  "q=brake&matchIn=oem,aftermarket",
  "q=brake&matchIn=all",
  "supplier=ib16-parts",
  "supplier=nope",
  "q=brake&system=brake-system&manufacturer=BOSCH&sort=price-asc",
  "q=0986&matchIn=part-number&minRating=3&returns=true"
]) {
  for (const who of ['anonymous', 'retail', 'admin']) {
    GROUPS.search.push([who, '/api/catalog/search' + (qs ? '?' + qs : '?q=')]);
  }
}

// The supplier pages, by the slug each one actually has, plus one that does
// not exist. A supplier with no parts at all is worth reaching if there is
// one: its breakdowns are empty and its fastest delivery is null, which is a
// different shape from a supplier that simply has few.
GROUPS.supplier = [];
for (const s of (await (await fetch(`${NODE}/api/suppliers`)).json()).suppliers) {
  GROUPS.supplier.push(['anonymous', `/api/suppliers/${s.slug}`]);
}
GROUPS.supplier.push(['anonymous', '/api/suppliers/no-such-supplier']);

// A pasted list of part numbers. Written as POSTs but it reads nothing but
// the catalogue, so it belongs with the comparisons rather than with the
// write tests.
GROUPS.bulk = [];
{
  const carried = search.products.slice(0, 3).map((p) => p.partNumber);
  const spaced = carried[0] ? carried[0].replace(/(.)(?=.)/, '$1 ') : 'x';
  for (const partNumbers of [
    carried,
    [spaced],
    [carried[0], carried[0]],                    // the same number twice
    ['  ', ''],                                  // nothing usable
    ['!!!'],                                     // nothing left after normalising
    ['NOSUCHPART'],
    [...carried, 'NOSUCHPART', spaced],
    [42, null, carried[0]],                      // not all of them are strings
    Array.from({ length: 1001 }, (_, i) => carried[i % carried.length]),
    // Numbers this catalogue does not stock, which resolve because one of its
    // parts lists them as a replacement — the path a customer holding a
    // competitor's catalogue actually takes.
    ['438588', 'DF4293', '34 11 6 794 917'],
    ['34116794917'],                             // the same, written closed up
    // Both at once: `ACP 34 000S` is a part in its own right AND a
    // cross-reference on another part, and the direct hit has to win.
    ['ACP 34 000S', '438588'],
  ]) {
    for (const who of ['anonymous', 'retail', 'admin']) {
      GROUPS.bulk.push([who, '/api/catalog/bulk', { partNumbers }]);
    }
  }
  GROUPS.bulk.push(['anonymous', '/api/catalog/bulk', { partNumbers: 'brake' }]);
  GROUPS.bulk.push(['anonymous', '/api/catalog/bulk', {}]);
}

GROUPS.account = [
  ['anonymous', '/api/cart'],
  ['retail', '/api/cart'],
  ['admin', '/api/cart'],
  ['anonymous', '/api/notifications'],
  ['retail', '/api/notifications'],
  ['admin', '/api/notifications'],
  ['anonymous', '/api/orders'],
  ['retail', '/api/orders'],
  ['admin', '/api/orders'],
];

GROUPS.admin = [];
for (const path of ['/api/admin/stats', '/api/admin/orders', '/api/admin/carts', '/api/admin/notifications',
  '/api/admin/suppliers', '/api/admin/warehouses', '/api/admin/outlets', '/api/admin/currencies',
  '/api/admin/client-categories', '/api/admin/markup-rules', '/api/admin/price-lists',
  '/api/admin/price-lists/no-such-list', '/api/admin/clients',
  '/api/admin/products', '/api/admin/products?q=brake', '/api/admin/products?q=bosch',
  '/api/admin/products/no-such-id/images', '/api/admin/products/no-such-id/stock']) {
  for (const who of ['anonymous', 'retail', 'admin']) GROUPS.admin.push([who, path]);
}

const only = process.argv[2];
const groups = only ? { [only]: GROUPS[only] ?? [] } : GROUPS;

let pass = 0, total = 0;

for (const [name, cases] of Object.entries(groups)) {
  if (cases.length === 0) continue;
  console.log(`\n${name}`);

  for (const [who, path, body] of cases) {
    total++;
    const cookie = cookies[who];
    if (cookie === null) { console.log(`  SKIP  ${who} could not sign in`); continue; }

    // A case with a body is a POST. Only for endpoints that write nothing —
    // the bulk lookup is the one, and it is here rather than in a write test
    // because it is a read wearing a POST for the size of its request.
    const options = {
      headers: { ...(cookie ? { cookie } : {}), ...(body ? { 'Content-Type': 'application/json' } : {}) },
      ...(body ? { method: 'POST', body: JSON.stringify(body) } : {}),
    };
    const [a, b] = await Promise.all([
      fetch(NODE + path, options).then((r) => r.json()).catch((e) => ({ threw: String(e) })),
      fetch(NET + path, options).then((r) => r.json()).catch((e) => ({ threw: String(e) })),
    ]);

    const label = body ? `${path} ${JSON.stringify(body).slice(0, 52)}` : path;
    const diff = firstDifference(a, b);
    if (!diff) { pass++; console.log(`  ok    ${who.padEnd(9)} ${label}`); }
    else {
      console.log(`  DIFF  ${who.padEnd(9)} ${label}`);
      console.log(`    at ${diff}`);
    }
  }
}

console.log(`\n${pass}/${total} identical`);
if (pass !== total) process.exitCode = 1;
