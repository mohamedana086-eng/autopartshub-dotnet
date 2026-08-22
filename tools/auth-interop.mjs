// Are the two APIs' session cookies interchangeable?
//
// HOW TO RUN
// ----------
//   node tools/auth-interop.mjs
//
// with the .NET API in Development on :5080 and the Node API on :3000, both
// signing with the same AUTH_SECRET.
//
// This matters more than it looks. Both APIs read the same database and during
// the port both may be reachable, so a customer signed in against one must not
// be signed out by the other — and an admin cookie minted by one must not be
// accepted by the other unless it genuinely verifies. The test is therefore
// both directions, plus the refusals.
import { installCsrf } from './csrf.mjs';

const NODE = 'http://localhost:3000';
const NET = 'http://localhost:5080';

// Both APIs refuse a write without a matching cross-site token. A browser
// gets that pairing for free; this makes every fetch below carry it too.
await installCsrf(NODE, NET);

const ADMIN = { email: 'admin@autopartshub.com', password: 'admin123' };

const cookieFrom = (res) =>
  (res.headers.getSetCookie() ?? [])
    .map((c) => c.split(';')[0])
    .find((c) => c.startsWith('aph_session='));

const login = async (base, creds = ADMIN) => {
  const res = await fetch(`${base}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(creds),
  });
  return { status: res.status, body: await res.json(), cookie: cookieFrom(res) };
};

const session = async (base, cookie) => {
  const res = await fetch(`${base}/api/auth/session`, {
    headers: cookie ? { cookie } : {},
  });
  return { status: res.status, body: await res.json() };
};

const line = (label, detail) => console.log(`  ${label.padEnd(46)} ${detail}`);

const node = await login(NODE);
const net = await login(NET);

console.log('signing in on each:');
line('node  /api/auth/login', `${node.status} ${JSON.stringify(node.body.user ?? node.body)}`);
line('dotnet /api/auth/login', `${net.status} ${JSON.stringify(net.body.user ?? net.body)}`);
line('login responses identical', JSON.stringify(node.body) === JSON.stringify(net.body) ? 'yes' : 'NO');

console.log('\nis each cookie accepted by the other API?');
const nodeOnNet = await session(NET, node.cookie);
const netOnNode = await session(NODE, net.cookie);
const nodeOnNode = await session(NODE, node.cookie);
const netOnNet = await session(NET, net.cookie);

line('node cookie -> node /session', JSON.stringify(nodeOnNode.body));
line('node cookie -> dotnet /session', JSON.stringify(nodeOnNet.body));
line('dotnet cookie -> node /session', JSON.stringify(netOnNode.body));
line('dotnet cookie -> dotnet /session', JSON.stringify(netOnNet.body));

const allFour = [nodeOnNode, nodeOnNet, netOnNode, netOnNet].map((r) => JSON.stringify(r.body));
line('all four agree', new Set(allFour).size === 1 ? 'yes' : 'NO');

console.log('\nrefusals:');
for (const [label, cookie] of [
  ['no cookie', undefined],
  ['empty', 'aph_session='],
  ['not a token', 'aph_session=garbage'],
  ['body without a signature', 'aph_session=eyJhIjoxfQ'],
  ['signature swapped for another', `aph_session=${node.cookie.split('.')[0].replace('aph_session=', '')}.${net.cookie.split('.')[1]}`],
  ['payload tampered, old signature', `${node.cookie.slice(0, -8)}AAAAAAAA`],
]) {
  const a = await session(NODE, cookie);
  const b = await session(NET, cookie);
  const bothRefuse = a.body.user === null && b.body.user === null;
  line(label, bothRefuse ? 'both refuse' : `node ${JSON.stringify(a.body)} / dotnet ${JSON.stringify(b.body)}`);
}

console.log('\nwrong credentials:');
for (const [label, creds] of [
  ['unknown address', { email: 'nobody@example.invalid', password: 'x' }],
  ['known address, wrong password', { email: ADMIN.email, password: 'wrong' }],
  ['no password', { email: ADMIN.email, password: '' }],
]) {
  const a = await login(NODE, creds);
  const b = await login(NET, creds);
  const same = a.status === b.status && JSON.stringify(a.body) === JSON.stringify(b.body);
  line(label, same ? `both ${a.status} ${JSON.stringify(a.body)}` : `node ${a.status} ${JSON.stringify(a.body)} / dotnet ${b.status} ${JSON.stringify(b.body)}`);
}
