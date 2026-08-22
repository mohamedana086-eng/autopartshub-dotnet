// The last three admin writes: uploading a price list, editing an account,
// and sending a notification.
//
// HOW TO RUN
// ----------
//   node tools/desk-writes.mjs
//
// with the .NET API in Development on :5080 and the Node API on :3000.
//
// These are the three that could not follow the create-and-delete pattern the
// other write tests use, and each needed its own way of leaving nothing
// behind:
//
//   price lists   have a delete, so each API uploads its own and removes it.
//   accounts      have no create, so one real account is edited and put back,
//                 with its original row printed first and compared at the end.
//   notifications have neither, so they go out for real and are taken back
//                 out through the development-only probe.
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

const line = (label, detail) => console.log(`  ${label.padEnd(38)} ${detail}`);

let same = 0, total = 0;

/** Sends the same request to both and compares status and body exactly. */
const both = async (label, path, method, body) => {
  total++;
  const [a, b] = await Promise.all([call(NODE, path, method, body), call(NET, path, method, body)]);
  const match = a.status === b.status && JSON.stringify(a.body) === JSON.stringify(b.body);
  if (match) same++;
  line(label, match
    ? `both ${a.status} ${JSON.stringify(a.body).slice(0, 60)}`
    : `node ${a.status} ${JSON.stringify(a.body).slice(0, 42)} | dotnet ${b.status} ${JSON.stringify(b.body).slice(0, 42)}`);
  return [a, b];
};

// Real part numbers, so a row can be made to match on purpose. Two of them,
// because the duplicate case needs a second part to prove it is not just
// reporting every repeat of the first.
const products = (await call(NODE, '/api/admin/products', 'GET')).body.products;
const [p1, p2] = products;
const currencies = (await call(NODE, '/api/admin/currencies', 'GET')).body.currencies;
const currenciesBefore = currencies.length;

// The catalogue may hold nothing but its base currency, and then the whole
// conversion path — the one that divides rather than multiplies, and the one
// that would silently misprice a catalogue if it were upside down — would
// never be exercised. So make one, quote a row in it, and take it away again.
const madeCurrency = await call(NODE, '/api/admin/currencies', 'POST',
  { code: 'ZZF', name: 'Probe francs', symbol: 'F', rate: 2.5, active: true });
const foreign = madeCurrency.status === 201
  ? madeCurrency.body.currency
  : currencies.find((c) => !c.isBase && c.rate !== 1);

const listsBefore = (await call(NODE, '/api/admin/price-lists', 'GET')).body.lists.length;
const notesBefore = (await call(NODE, '/api/admin/notifications', 'GET')).body.notifications.length;

/* ------------------------------------------------- price lists, refused --- */

console.log('price-list uploads that are refused:');
await both('no name', '/api/admin/price-lists', 'POST', { rows: [] });
await both('name of 121 characters', '/api/admin/price-lists', 'POST', { name: 'x'.repeat(121) });
await both('no rows key', '/api/admin/price-lists', 'POST', { name: 'ZZ probe' });
await both('rows is an object', '/api/admin/price-lists', 'POST', { name: 'ZZ probe', rows: {} });
await both('rows is empty', '/api/admin/price-lists', 'POST', { name: 'ZZ probe', rows: [] });
await both('a row is a number', '/api/admin/price-lists', 'POST', { name: 'ZZ probe', rows: [5] });
await both('a row is null', '/api/admin/price-lists', 'POST', { name: 'ZZ probe', rows: [null] });
await both('every part unknown', '/api/admin/price-lists', 'POST',
  { name: 'ZZ probe', rows: [{ partNumber: 'NOSUCHPART1', price: 1 }, { partNumber: 'NOSUCHPART2', price: 2 }] });
await both('every price unusable', '/api/admin/price-lists', 'POST',
  { name: 'ZZ probe', rows: [{ partNumber: p1.partNumber, price: 'free' }, { partNumber: p2.partNumber, price: -1 }] });
await both('mixed reasons, none usable', '/api/admin/price-lists', 'POST',
  { name: 'ZZ probe', rows: [
    { partNumber: 'NOSUCHPART1', price: 1 },
    { partNumber: 'NOSUCHPART2', price: 1 },
    { partNumber: p1.partNumber, price: 'free' },
  ] });
await both('every row blank', '/api/admin/price-lists', 'POST',
  { name: 'ZZ probe', rows: [{ partNumber: '  ' }, { partNumber: '' }] });
await both('unknown currency on every row', '/api/admin/price-lists', 'POST',
  { name: 'ZZ probe', rows: [{ partNumber: p1.partNumber, price: 1, currency: 'ZZQ' }] });
await both('patch an unknown list', '/api/admin/price-lists/nope', 'PATCH', { name: 'X' });
await both('delete an unknown list', '/api/admin/price-lists/nope', 'DELETE');

/* ------------------------------------------------- price lists, uploaded --- */

console.log('\nprice-list round trips (each API uploads its own, then deletes it):');

// Deliberately awkward: a part written with separators the catalogue does not
// use, the same part twice, a blank line, a part nobody stocks, a price that
// is not a number, and — if there is one — a foreign currency to convert.
const spaced = p1.partNumber.replace(/(.)(?=.)/, '$1 ');
const rows = [
  { partNumber: spaced, price: 10 },
  { partNumber: p2.partNumber, price: 7.555 },
  { partNumber: '   ', price: 3 },
  { partNumber: 'NOSUCHPART', price: 4 },
  { partNumber: p2.partNumber, price: 8.125 },
  { partNumber: p1.partNumber, price: 'gratis' },
  // Quoted in the base currency by name: recorded as a plain price, not as a
  // conversion of itself.
  { partNumber: products[3].partNumber, price: 5, currency: currencies.find((c) => c.isBase).code },
  // Quoted in a foreign one, lowercase, and at a rate that does not divide
  // evenly — 99.99 / 2.5 is 39.996, which has to land as 40.00 and keep 99.99
  // as what the file said.
  ...(foreign ? [{ partNumber: products[2].partNumber, price: 99.99, currency: foreign.code.toLowerCase() }] : []),
];

const upload = async (base, label) => {
  const made = await call(base, '/api/admin/price-lists', 'POST',
    { name: `ZZ probe list`, description: ' a probe ', sourceName: 'probe.csv', rows });
  if (made.status !== 201) { line(`${label} upload`, `FAILED ${made.status} ${JSON.stringify(made.body)}`); return null; }
  line(`${label} upload`, `201 accepted ${made.body.accepted}, rejected ${made.body.rejectedCount}`);

  const id = made.body.list.id;

  // What actually landed, so the conversion and the rounding are visible
  // rather than inferred from a count.
  const read = await call(base, `/api/admin/price-lists/${id}`, 'GET');
  line(`${label} contents`, `${read.body.items.length} lines`);
  for (const i of read.body.items) {
    console.log(`      ${i.partNumber.padEnd(16)} ${String(i.price).padStart(8)}`
      + `${i.sourcePrice === null ? '' : `  <- ${i.sourcePrice} ${i.sourceCurrency}`}`);
  }

  const named = await call(base, `/api/admin/price-lists/${id}`, 'PATCH', { name: 'ZZ probe renamed', description: '' });
  line(`${label} rename`, `${named.status} ${JSON.stringify(named.body.list ?? named.body).slice(0, 90)}`);

  // Switching it on is the interesting half: at most one list may be active,
  // so this has to stand the real one down and put it back afterwards.
  const on = await call(base, `/api/admin/price-lists/${id}`, 'PATCH', { active: true });
  line(`${label} activate`, `${on.status} active=${on.body.list?.active}`);

  const refused = await call(base, `/api/admin/price-lists/${id}`, 'DELETE');
  line(`${label} delete while active`, `${refused.status} ${JSON.stringify(refused.body).slice(0, 80)}`);

  const off = await call(base, `/api/admin/price-lists/${id}`, 'PATCH', { active: false });
  line(`${label} deactivate`, `${off.status} active=${off.body.list?.active}`);

  const gone = await call(base, `/api/admin/price-lists/${id}`, 'DELETE');
  line(`${label} delete`, `${gone.status} ${JSON.stringify(gone.body)}`);

  return {
    accepted: made.body.accepted,
    rejectedCount: made.body.rejectedCount,
    rejected: made.body.rejected,
    list: made.body.list,
    items: read.body.items,
    renamed: named.body.list,
    activated: on.body.list,
    refusal: refused,
    deactivated: off.body.list,
  };
};

// Whatever list was active before any of this, so it can be put back.
const activeBefore = (await call(NODE, '/api/admin/price-lists', 'GET')).body.lists.find((l) => l.active);

const a = await upload(NODE, 'node  ');
const b = await upload(NET, 'dotnet');

/** Ids and timestamps differ by construction; everything else must not. */
const scrub = (v) => JSON.parse(JSON.stringify(v ?? null, (k, x) =>
  k === 'id' || k === 'createdAt' || k === 'updatedAt' ? undefined : x));

total++;
const uploadsMatch = JSON.stringify(scrub(a)) === JSON.stringify(scrub(b));
if (uploadsMatch) same++;
line('price lists: both APIs agree', uploadsMatch ? 'yes' : 'NO');
if (!uploadsMatch) {
  console.log('    node   ', JSON.stringify(scrub(a)).slice(0, 400));
  console.log('    dotnet ', JSON.stringify(scrub(b)).slice(0, 400));
}

if (activeBefore) {
  const restored = await call(NODE, `/api/admin/price-lists/${activeBefore.id}`, 'PATCH', { active: true });
  line('put the real active list back', `${restored.status} ${restored.body.list?.name} active=${restored.body.list?.active}`);
}

if (madeCurrency.status === 201) {
  const gone = await call(NODE, `/api/admin/currencies/${madeCurrency.body.currency.id}`, 'DELETE');
  line('remove the probe currency', `${gone.status} ${JSON.stringify(gone.body)}`);
}

/* ------------------------------------------------------------- accounts --- */

console.log('\naccount edits that are refused:');
const clients = (await call(NODE, '/api/admin/clients', 'GET')).body.clients;
const tiers = (await call(NODE, '/api/admin/client-categories', 'GET')).body.categories;
const subject = clients.find((c) => c.role === 'RETAIL');
const salesperson = clients.find((c) => c.role === 'SALES');

await both('unknown role', `/api/admin/clients/${subject.id}`, 'PATCH', { role: 'WIZARD' });
await both('no role at all', `/api/admin/clients/${subject.id}`, 'PATCH', {});
await both('unknown tier', `/api/admin/clients/${subject.id}`, 'PATCH', { role: 'RETAIL', categoryId: 'nope' });
await both('unknown account', '/api/admin/clients/nope', 'PATCH', { role: 'RETAIL' });
await both('discount 150', `/api/admin/clients/${subject.id}`, 'PATCH', { role: 'RETAIL', discountPercent: 150 });
await both('discount "lots"', `/api/admin/clients/${subject.id}`, 'PATCH', { role: 'RETAIL', discountPercent: 'lots' });
await both('unknown currency', `/api/admin/clients/${subject.id}`, 'PATCH', { role: 'RETAIL', currencyId: 'nope' });
await both('its own sales manager', `/api/admin/clients/${subject.id}`, 'PATCH', { role: 'RETAIL', salesManagerId: subject.id });
await both('unknown sales manager', `/api/admin/clients/${subject.id}`, 'PATCH', { role: 'RETAIL', salesManagerId: 'nope' });
if (subject) {
  await both('a customer as sales manager', `/api/admin/clients/${subject.id}`, 'PATCH',
    { role: 'RETAIL', salesManagerId: clients.find((c) => c.role !== 'SALES' && c.id !== subject.id).id });
}

// An inactive currency, made for the purpose and taken away again, because
// "activate it before quoting anyone in it" is a sentence with a code in it.
const parked = await call(NODE, '/api/admin/currencies', 'POST',
  { code: 'ZZQ', name: 'Probe currency', symbol: 'Z', rate: 2, active: false });
if (parked.status === 201) {
  await both('an inactive currency', `/api/admin/clients/${subject.id}`, 'PATCH',
    { role: 'RETAIL', currencyId: parked.body.currency.id });
  await call(NODE, `/api/admin/currencies/${parked.body.currency.id}`, 'DELETE');
}

console.log('\none real account, edited by each API and put back:');
console.log(`  original: ${JSON.stringify(subject)}`);

const restore = {
  role: subject.role,
  categoryId: subject.categoryId,
  discountPercent: subject.discountPercent,
  currencyId: subject.currencyId,
  salesManagerId: subject.salesManagerId,
};

const otherTier = tiers.find((t) => t.id !== subject.categoryId) ?? tiers[0];
const edit = {
  role: 'B2B',
  categoryId: otherTier.id,
  discountPercent: 12.5,
  currencyId: currencies.find((c) => c.active)?.id ?? null,
  salesManagerId: salesperson?.id ?? null,
};

const edited = [];
for (const [base, label] of [[NODE, 'node  '], [NET, 'dotnet']]) {
  const r = await call(base, `/api/admin/clients/${subject.id}`, 'PATCH', edit);
  line(`${label} edit`, `${r.status} ${JSON.stringify(r.body.client ?? r.body).slice(0, 100)}`);
  edited.push(r);
  await call(NODE, `/api/admin/clients/${subject.id}`, 'PATCH', restore);
}

total++;
const editsMatch = edited[0].status === edited[1].status
  && JSON.stringify(edited[0].body) === JSON.stringify(edited[1].body);
if (editsMatch) same++;
line('account edit: both APIs agree', editsMatch ? 'yes' : 'NO');

const after = (await call(NODE, '/api/admin/clients', 'GET')).body.clients.find((c) => c.id === subject.id);
const putBack = JSON.stringify(after) === JSON.stringify(subject);
line('account put back exactly', putBack ? 'yes' : `NO — now ${JSON.stringify(after)}`);

/* --------------------------------------------------------- notifications --- */

console.log('\nnotifications that are refused:');
await both('no recipient', '/api/admin/notifications', 'POST', { title: 'X' });
await both('no title', '/api/admin/notifications', 'POST', { clientId: subject.id });
await both('title of 201 characters', '/api/admin/notifications', 'POST', { clientId: subject.id, title: 'x'.repeat(201) });
await both('unknown account', '/api/admin/notifications', 'POST', { clientId: 'nope', title: 'X' });
await both('an off-site link', '/api/admin/notifications', 'POST', { clientId: subject.id, title: 'X', link: 'https://evil.example' });
await both('a protocol-relative link', '/api/admin/notifications', 'POST', { clientId: subject.id, title: 'X', link: '//evil.example' });
await both('a backslash link', '/api/admin/notifications', 'POST', { clientId: subject.id, title: 'X', link: '/\\evil.example' });

console.log('\none real notification from each API, then taken back out:');
const sent = [];
for (const [base, label] of [[NODE, 'node  '], [NET, 'dotnet']]) {
  const r = await call(base, '/api/admin/notifications', 'POST',
    { clientId: subject.id, title: 'ZZ probe', body: ' a probe ', link: '/orders', type: 'nonsense' });
  line(`${label} send`, `${r.status} ${JSON.stringify(r.body.notification ?? r.body).slice(0, 100)}`);
  sent.push(r);
}

total++;
const sentMatch = sent[0].status === sent[1].status
  && JSON.stringify(scrub(sent[0].body)) === JSON.stringify(scrub(sent[1].body));
if (sentMatch) same++;
line('notification: both APIs agree', sentMatch ? 'yes' : 'NO');

for (const r of sent) {
  const id = r.body?.notification?.id;
  if (!id) continue;
  const gone = await fetch(`${NET}/dev/forget-notification`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ notificationId: id }),
  }).then((x) => x.json());
  line('unsent', `${id} removed ${gone.removed}`);
}

/* ------------------------------------------------------ nothing left over --- */

console.log('\nrow counts before -> after:');
let clean = putBack;
if (!putBack) console.log('  the edited account was NOT put back');
for (const [label, path, key] of [
  ['price lists', '/api/admin/price-lists', 'lists'],
  ['notifications', '/api/admin/notifications', 'notifications'],
  ['currencies', '/api/admin/currencies', 'currencies'],
]) {
  const before = { 'price lists': listsBefore, notifications: notesBefore, currencies: currenciesBefore }[label];
  const now = (await call(NODE, path, 'GET')).body[key].length;
  if (now !== before) clean = false;
  console.log(`  ${label.padEnd(14)} ${before} -> ${now}  ${now === before ? 'ok' : 'LEFTOVER'}`);
}

const activeNow = (await call(NODE, '/api/admin/price-lists', 'GET')).body.lists.find((l) => l.active);
const activeOk = (activeBefore?.id ?? null) === (activeNow?.id ?? null);
if (!activeOk) clean = false;
console.log(`  active list    ${activeBefore?.name ?? 'none'} -> ${activeNow?.name ?? 'none'}  ${activeOk ? 'ok' : 'CHANGED'}`);

console.log(`\n${same}/${total} identical${clean ? '' : '  — AND SOMETHING WAS LEFT BEHIND'}`);
if (!clean || same !== total) process.exitCode = 1;
