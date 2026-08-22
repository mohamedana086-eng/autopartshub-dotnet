// Supplier self-registration and approval, on both APIs.
//
// HOW TO RUN
// ----------
//   node tools/supplier-signup.mjs
//
// with the .NET API in Development on :5080 and the Node API on :3000.
//
// The interesting part is not the response shapes — it is that a supplier
// switched off in one API is invisible in the other. Both read the same rows,
// so a filter missing from one of the eight customer-facing queries shows up
// here as a part on sale that should not be.
//
// So the flow crosses over deliberately: registered through Node, checked on
// both, approved through .NET, checked on both again.
const NODE = 'http://localhost:3000';
const NET = 'http://localhost:5080';

const login = async (base, creds) => {
  const r = await fetch(`${base}/api/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(creds),
  });
  return r.headers.getSetCookie().map((c) => c.split(';')[0]).join('; ');
};
const admin = await login(NODE, { email: 'admin@autopartshub.com', password: 'admin123' });

const call = (base, path, method, body, cookie) =>
  fetch(base + path, {
    method: method ?? 'GET',
    headers: { 'Content-Type': 'application/json', ...(cookie ? { cookie } : {}) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  }).then(async (r) => ({
    status: r.status,
    cookie: r.headers.getSetCookie().map((c) => c.split(';')[0]).join('; '),
    body: await r.json().catch(() => null),
  }));

const line = (label, detail) => console.log(`  ${label.padEnd(38)} ${detail}`);

let same = 0, total = 0;

const both = async (label, path, method, body, cookie) => {
  total++;
  const [a, b] = await Promise.all([
    call(NODE, path, method, body, cookie), call(NET, path, method, body, cookie),
  ]);
  const match = a.status === b.status && JSON.stringify(a.body) === JSON.stringify(b.body);
  if (match) same++;
  line(label, match
    ? `both ${a.status} ${JSON.stringify(a.body).slice(0, 58)}`
    : `node ${a.status} ${JSON.stringify(a.body).slice(0, 40)} | dotnet ${b.status} ${JSON.stringify(b.body).slice(0, 40)}`);
  return [a, b];
};

const check = (label, ok, detail = '') => {
  total++;
  if (ok) same++;
  line(label, ok ? `yes ${detail}` : `NO ${detail}`);
};

const suppliersBefore = (await call(NODE, '/api/admin/suppliers', 'GET', undefined, admin)).body.suppliers.length;
const clientsBefore = (await call(NODE, '/api/admin/clients', 'GET', undefined, admin)).body.clients.length;

const made = [];
let productId = null;

try {
  /* ------------------------------------------------------- refusals --- */

  console.log('registrations that are refused:');
  await both('nothing at all', '/api/suppliers/register', 'POST', {});
  await both('no code', '/api/suppliers/register', 'POST',
    { company: 'X', email: 'a@b.invalid', password: 'aaaaaa' });
  await both('no company', '/api/suppliers/register', 'POST',
    { code: 'ZZQ', email: 'a@b.invalid', password: 'aaaaaa' });
  await both('a five character password', '/api/suppliers/register', 'POST',
    { company: 'X', code: 'ZZQ', email: 'a@b.invalid', password: 'aaaaa' });
  await both('a code with a space', '/api/suppliers/register', 'POST',
    { company: 'X', code: 'ZZ Q', email: 'a@b.invalid', password: 'aaaaaa' });
  await both('a code with a slash', '/api/suppliers/register', 'POST',
    { company: 'X', code: 'ZZ/Q', email: 'a@b.invalid', password: 'aaaaaa' });
  await both('a code of 13 characters', '/api/suppliers/register', 'POST',
    { company: 'X', code: 'ZZQQQQQQQQQQQ', email: 'a@b.invalid', password: 'aaaaaa' });
  await both('a company name with no letters', '/api/suppliers/register', 'POST',
    { company: '!!!', code: 'ZZQ', email: 'a@b.invalid', password: 'aaaaaa' });
  await both('an address already registered', '/api/suppliers/register', 'POST',
    { company: 'X', code: 'ZZQ', email: 'admin@autopartshub.com', password: 'aaaaaa' });
  await both('a code already held', '/api/suppliers/register', 'POST',
    { company: 'Some Other Firm', code: 'IB16', email: 'zz-clash@probe.invalid', password: 'aaaaaa' });
  await both('a company name already held', '/api/suppliers/register', 'POST',
    { company: 'IB16 Parts', code: 'ZZFREE', email: 'zz-clash@probe.invalid', password: 'aaaaaa' });

  console.log('\napproval calls that are refused:');
  await both('not signed in', '/api/admin/suppliers/nope/approval', 'PATCH', { active: true });
  await both('active is a string', '/api/admin/suppliers/nope/approval', 'PATCH', { active: 'yes' }, admin);
  await both('an unknown supplier', '/api/admin/suppliers/nope/approval', 'PATCH', { active: true }, admin);
  await both('the waiting list, not signed in', '/api/admin/suppliers/waiting', 'GET');

  /* --------------------------------------------- one from each API --- */

  console.log('\none real application through each API:');
  for (const [base, label, code, email] of [
    [NODE, 'node  ', 'ZZSIGNN', 'zz-signup-node@probe.invalid'],
    [NET, 'dotnet', 'ZZSIGND', 'zz-signup-dotnet@probe.invalid'],
  ]) {
    const r = await call(base, '/api/suppliers/register', 'POST', {
      company: `ZZ Signup ${label.trim()} Co`,
      code: code.toLowerCase(),
      email: email.toUpperCase(),
      password: 'probe-password',
      country: 'Nowhere',
      description: 'A probe.',
    });
    line(`${label} register`, `${r.status} ${JSON.stringify(r.body).slice(0, 76)}`);
    made.push({ base, label, code, email, ...r });
  }

  // Ids and the fields the two deliberately differ by are removed; everything
  // else has to be the same sentence, the same shape, the same order.
  const scrub = (v) => JSON.parse(JSON.stringify(v ?? null, (k, x) =>
    k === 'id' || k === 'email' || k === 'code' || k === 'slug' || k === 'name' ? undefined : x));
  check('register: both APIs agree',
    made[0].status === made[1].status
    && JSON.stringify(scrub(made[0].body)) === JSON.stringify(scrub(made[1].body)));

  check('both arrive switched off',
    made.every((m) => m.body?.supplier?.active === false));
  check('both say waiting', made.every((m) => m.body?.status === 'waiting'));
  check('both upper-case the code',
    made.every((m) => m.body?.supplier?.code === m.code));
  check('both sign the applicant in as SUPPLIER',
    made.every((m) => m.body?.user?.role === 'SUPPLIER'));

  // The session cookie each API issued has to be readable by the other — the
  // same interop the login already has, on a role neither had seen before.
  for (const m of made) {
    for (const [base, who] of [[NODE, 'node  '], [NET, 'dotnet']]) {
      const s = await call(base, '/api/auth/session', 'GET', undefined, m.cookie);
      check(`${who} reads the ${m.label.trim()} session`, s.body?.user?.role === 'SUPPLIER',
        s.body?.user?.role ?? JSON.stringify(s.body));
    }
  }

  /* ------------------------------------------- the waiting list ----- */

  console.log('\nthe waiting list:');
  const [wa, wb] = await both('both APIs list the same applications',
    '/api/admin/suppliers/waiting', 'GET', undefined, admin);
  check('both are on it',
    made.every((m) => wa.body.suppliers.some((s) => s.id === m.body.supplier.id)));

  /* ------------------------------- hidden while they wait, on both --- */

  const node_ = made[0];
  const refs = await call(NODE, '/api/admin/products', 'GET', undefined, admin);
  const part = await call(NODE, '/api/admin/products', 'POST', {
    partNumber: 'ZZ-CROSS-PART', name: 'Cross probe part', basePrice: 10,
    manufacturerId: refs.body.manufacturers[0].id,
    vehicleSystemId: refs.body.systems[0].id,
    supplierId: node_.body.supplier.id,
  }, admin);
  productId = part.body?.product?.id ?? null;
  check('a part was given to the waiting supplier', part.status === 201, part.body?.error ?? '');

  /** Every customer-facing surface on one API, asked whether it can see it. */
  const visible = async (base) => ({
    search: (await call(base, '/api/catalog/search?q=ZZ-CROSS-PART')).body.products.length,
    listing: (await call(base, '/api/catalog/search?q=')).body.products
      .filter((p) => p.id === productId).length,
    detail: (await call(base, `/api/catalog/products/${productId}`)).status,
    bulk: (await call(base, '/api/catalog/bulk', 'POST', { partNumbers: ['ZZ-CROSS-PART'] }))
      .body.rows?.[0]?.found,
    directory: (await call(base, '/api/suppliers')).body.suppliers
      .filter((s) => s.id === node_.body.supplier.id).length,
    page: (await call(base, `/api/suppliers/${node_.body.supplier.slug}`)).status,
    basket: (await call(base, '/api/cart', 'PUT', { items: [{ productId, quantity: 1 }] }, admin))
      .body.items?.length ?? 0,
  });

  console.log('\nwhile they wait — the same answer from both:');
  const hiddenA = await visible(NODE);
  const hiddenB = await visible(NET);
  line('node  ', JSON.stringify(hiddenA));
  line('dotnet', JSON.stringify(hiddenB));
  check('both hide it identically', JSON.stringify(hiddenA) === JSON.stringify(hiddenB));
  check('and it really is hidden',
    JSON.stringify(hiddenA) === JSON.stringify(
      { search: 0, listing: 0, detail: 404, bulk: false, directory: 0, page: 404, basket: 0 }),
    JSON.stringify(hiddenA));

  /* ------------------------- approved on one API, seen on both ------- */

  console.log('\napproved through .NET, and the crossover:');
  const ap = await call(NET, `/api/admin/suppliers/${node_.body.supplier.id}/approval`, 'PATCH',
    { active: true }, admin);
  line('dotnet approves', `${ap.status} action=${ap.body?.action} approvedAt=${ap.body?.supplier?.approvedAt}`);
  check('reported as approved', ap.body?.action === 'approved');
  check('approving twice is refused',
    (await call(NET, `/api/admin/suppliers/${node_.body.supplier.id}/approval`, 'PATCH',
      { active: true }, admin)).status === 409);

  const shownA = await visible(NODE);
  const shownB = await visible(NET);
  line('node  ', JSON.stringify(shownA));
  line('dotnet', JSON.stringify(shownB));
  check('both show it identically', JSON.stringify(shownA) === JSON.stringify(shownB));
  check('and it really is on sale',
    JSON.stringify(shownA) === JSON.stringify(
      { search: 1, listing: 1, detail: 200, bulk: true, directory: 1, page: 200, basket: 1 }),
    JSON.stringify(shownA));

  /* ---------------------------- suspend and reinstate, both APIs ----- */

  console.log('\nsuspended through Node, reinstated through .NET:');
  const off = await call(NODE, `/api/admin/suppliers/${node_.body.supplier.id}/approval`, 'PATCH',
    { active: false }, admin);
  check('node reports a suspension', off.body?.action === 'suspended', off.body?.action);
  check('hidden again on .NET', (await visible(NET)).search === 0);
  check('not back on the waiting list',
    !(await call(NET, '/api/admin/suppliers/waiting', 'GET', undefined, admin))
      .body.suppliers.some((s) => s.id === node_.body.supplier.id));

  const back = await call(NET, `/api/admin/suppliers/${node_.body.supplier.id}/approval`, 'PATCH',
    { active: true }, admin);
  check('.NET reports a reinstatement', back.body?.action === 'reinstated', back.body?.action);
  check('the original approval date survived',
    back.body?.supplier?.approvedAt === ap.body?.supplier?.approvedAt,
    `${back.body?.supplier?.approvedAt} vs ${ap.body?.supplier?.approvedAt}`);

  // The admin supplier list carries the two new columns now, so both APIs have
  // to agree on them for a row that has moved through every state.
  const rowOf = async (base) => (await call(base, '/api/admin/suppliers', 'GET', undefined, admin))
    .body.suppliers.find((s) => s.id === node_.body.supplier.id);
  const [ra, rb] = [await rowOf(NODE), await rowOf(NET)];
  check('the admin row matches, field for field', JSON.stringify(ra) === JSON.stringify(rb),
    JSON.stringify(ra) === JSON.stringify(rb) ? '' : `\n      node   ${JSON.stringify(ra)}\n      dotnet ${JSON.stringify(rb)}`);
} finally {
  console.log('\nclean-up:');
  await call(NODE, '/api/cart', 'PUT', { items: [] }, admin);

  if (productId) {
    await call(NODE, `/api/admin/products/${productId}/stock`, 'PUT', { levels: [] }, admin);
    line('part removed', (await call(NODE, `/api/admin/products/${productId}`, 'DELETE', undefined, admin)).status);
  }
  for (const m of made) {
    const sid = m.body?.supplier?.id;
    const cid = m.body?.user?.id;
    if (sid) line(`${m.label} supplier removed`,
      (await call(NODE, `/api/admin/suppliers/${sid}`, 'DELETE', undefined, admin)).status);
    if (cid) line(`${m.label} account removed`,
      JSON.stringify((await call(NET, '/dev/forget-client', 'POST', { clientId: cid })).body));
  }
  // The admins' "has applied to supply" notices, which nothing else removes.
  const notes = (await call(NODE, '/api/admin/notifications', 'GET', undefined, admin)).body.notifications;
  for (const n of notes.filter((x) => x.link === '/admin/suppliers/waiting')) {
    await call(NET, '/dev/forget-notification', 'POST', { notificationId: n.id });
  }
  line('application notices removed', notes.filter((x) => x.link === '/admin/suppliers/waiting').length);

  const suppliersAfter = (await call(NODE, '/api/admin/suppliers', 'GET', undefined, admin)).body.suppliers.length;
  const clientsAfter = (await call(NODE, '/api/admin/clients', 'GET', undefined, admin)).body.clients.length;
  const clean = suppliersAfter === suppliersBefore && clientsAfter === clientsBefore;

  console.log('\nrow counts before -> after:');
  console.log(`  suppliers  ${suppliersBefore} -> ${suppliersAfter}  ${suppliersAfter === suppliersBefore ? 'ok' : 'LEFTOVER'}`);
  console.log(`  clients    ${clientsBefore} -> ${clientsAfter}  ${clientsAfter === clientsBefore ? 'ok' : 'LEFTOVER'}`);

  console.log(`\n${same}/${total} identical${clean ? '' : '  — AND SOMETHING WAS LEFT BEHIND'}`);
  process.exitCode = !clean || same !== total ? 1 : 0;
}
