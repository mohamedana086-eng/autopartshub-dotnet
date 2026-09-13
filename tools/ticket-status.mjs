// Who may set a ticket's status, and who may ask to hear about one.
//
// HOW TO RUN
// ----------
//   node tools/ticket-status.mjs
//
// with the .NET API in Development on :5080, signed-in-able as the seeded
// admin. One API: PATCH /api/tickets/{id}/status and PUT .../following are
// the shape the backlog specifies and the Node API does not serve them yet.
//
// WHY A HARNESS
// -------------
// Everything interesting here is about who is asking. A customer may settle
// their own ticket and may not claim we answered it; a customer may not touch
// somebody else's at all, and is told it does not exist rather than that they
// may not have it. None of that is reachable without two accounts, a session
// each, and a server to hold the distinction — which is a harness, not a unit
// test.
//
// It makes its own two customers and its own ticket. The ticket is removed;
// the accounts stay, named @probe.invalid, the same way account-order.mjs
// leaves its own.

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

const adminSignIn = await call('/api/auth/login', 'POST',
  { email: 'admin@autopartshub.com', password: 'admin123' });

if (adminSignIn.status !== 200) {
  console.error(`could not sign in as the seeded admin: ${adminSignIn.status}`);
  process.exit(1);
}

const admin = adminSignIn.cookies;

// ------------------------------------------------------------ two customers

const stamp = Date.now();

async function register(who) {
  const r = await call('/api/auth/register', 'POST', {
    name: `ZZ ${who}`,
    email: `zz-ticket-${who}-${stamp}@probe.invalid`,
    password: 'probe-password',
    role: 'RETAIL',
  });

  if (r.status !== 201) {
    console.error(`could not register ${who}:`, JSON.stringify(r.body));
    process.exit(1);
  }

  return r.cookies;
}

const mine = await register('owner');
const theirs = await register('stranger');

// ---------------------------------------------------------------- a ticket

const made = await call('/api/tickets', 'POST',
  { subject: `Probe ticket ${stamp}`, body: 'Does this arrive?' }, mine);

const ticketId = made.body?.ticket?.id;

if (!ticketId) {
  console.error('could not raise the probe ticket:', JSON.stringify(made.body));
  process.exit(1);
}

console.log(`probe ticket ${ticketId}\n`);

const statusOf = async (cookie) => (await call(`/api/tickets/${ticketId}`, 'GET', undefined, cookie))
  .body?.ticket?.status;

try {
  // ------------------------------------------------------- what a customer may

  expect('a new ticket is open', await statusOf(mine), 'open');

  let r = await call(`/api/tickets/${ticketId}/status`, 'PATCH', { status: 'resolved' }, mine);
  expect('the customer may settle their own ticket', r.status, 200);
  expect('  and it took', await statusOf(mine), 'resolved');

  r = await call(`/api/tickets/${ticketId}/status`, 'PATCH', { status: 'open' }, mine);
  expect('the customer may reopen it', await statusOf(mine), 'open');

  // The one they may not claim. "We answered you" is produced by a staff
  // message arriving, and letting a customer say it would take their own
  // ticket off the queue of things waiting on us.
  r = await call(`/api/tickets/${ticketId}/status`, 'PATCH', { status: 'answered' }, mine);
  expect('the customer may not mark it answered', r.status, 403);
  expect('  and nothing moved', await statusOf(mine), 'open');

  // -------------------------------------------------------- what staff may

  r = await call(`/api/tickets/${ticketId}/status`, 'PATCH', { status: 'answered' }, admin);
  expect('staff may mark it answered', r.status, 200);
  expect('  and it took', await statusOf(mine), 'answered');

  // ------------------------------------------------------ somebody else's

  r = await call(`/api/tickets/${ticketId}/status`, 'PATCH', { status: 'resolved' }, theirs);
  expect("a stranger is told it does not exist", r.status, 404);
  expect('  and nothing moved', await statusOf(mine), 'answered');

  r = await call(`/api/tickets/${ticketId}/status`, 'PATCH', { status: 'resolved' });
  expect('an anonymous caller is refused', r.status, 401);

  // ------------------------------------------------------------- refusals

  r = await call(`/api/tickets/${ticketId}/status`, 'PATCH', { status: 'sideways' }, admin);
  expect('a status nothing recognises is refused', r.status, 400);

  // ------------------------------------------------------------ following

  r = await call(`/api/tickets/${ticketId}/following`, 'PUT', { following: true }, admin);
  expect('staff may follow a ticket', r.body?.following, true);
  expect('  and it is counted', r.body?.followers, 1);

  r = await call(`/api/tickets/${ticketId}/following`, 'PUT', { following: true }, admin);
  expect('following twice is following once', r.body?.followers, 1);

  r = await call(`/api/tickets/${ticketId}/following`, 'PUT', { following: true }, mine);
  expect('the customer may follow their own', r.body?.followers, 2);

  r = await call(`/api/tickets/${ticketId}/following`, 'PUT', { following: false }, admin);
  expect('and unfollow', r.body?.followers, 1);

  r = await call(`/api/tickets/${ticketId}/following`, 'PUT', { following: false }, admin);
  expect('unfollowing twice is not an error', r.status, 200);

  r = await call(`/api/tickets/${ticketId}/following`, 'PUT', { following: true }, theirs);
  expect('a stranger cannot follow it', r.status, 404);

  r = await call(`/api/tickets/${ticketId}/following`, 'PUT', { following: 'yes' }, admin);
  expect('following must be a boolean', r.status, 400);
} finally {
  // The ticket goes, and its followers with it — the cascade is the point.
  const gone = await call(`/api/admin/tickets/${ticketId}`, 'DELETE', undefined, admin);

  if (gone.status === 404 || gone.status === 405) {
    console.log(`\n  --    no endpoint deletes a ticket; ${ticketId} stays, named "Probe ticket".`);
  } else if (gone.status !== 200 && gone.status !== 204) {
    bad('the probe ticket was removed', `DELETE answered ${gone.status}`);
  } else {
    line('the probe ticket was removed');
  }
}

console.log(failures === 0 ? '\nall passed' : `\n${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
