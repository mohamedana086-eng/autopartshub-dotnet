// The cross-site token, for scripts that talk to the APIs like a browser does.
//
// The APIs refuse any POST, PUT, PATCH or DELETE whose X-XSRF-TOKEN header
// does not match its XSRF-TOKEN cookie. A browser gets that pairing for free —
// the server sets the cookie, Angular copies it into the header — so a script
// has to do both halves itself.
//
// Doing it rather than exempting scripts is the point. An exemption would have
// to be something the server can recognise about a request, and everything a
// server can recognise about a request is something a forged request can also
// claim. The scripts become slightly more faithful instead, which is what a
// test of a browser-facing API should be.
//
// USAGE
// -----
//   import { installCsrf } from './csrf.mjs';
//   await installCsrf(NODE, NET);
//
// One line, before anything else runs. It wraps global fetch rather than
// asking every call site to remember, because there are two dozen of them
// across these scripts and the one that gets forgotten is a red test nobody
// can explain.

const COOKIE = 'XSRF-TOKEN';
const HEADER = 'X-XSRF-TOKEN';
const GUARDED = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

/** Fetches one API's token, or null if it does not issue one. */
async function tokenFor(base, rawFetch) {
  const res = await rawFetch(`${base}/api/systems`);
  const set = res.headers.getSetCookie().map((c) => c.split(';')[0]);
  const cookie = set.find((c) => c.startsWith(`${COOKIE}=`));
  return cookie ? cookie.slice(COOKIE.length + 1) : null;
}

/**
 * Makes every mutating request from this process carry a matching pair.
 *
 * Tokens are per API, because each issues its own. An API that does not issue
 * one is left alone, so a script works against a version from before this
 * existed — and against the two APIs while only one of them has been updated.
 */
export async function installCsrf(...bases) {
  const rawFetch = globalThis.fetch;
  const tokens = new Map();

  for (const base of bases) {
    const token = await tokenFor(base, rawFetch);
    if (token) tokens.set(base, token);
  }

  globalThis.fetch = (input, init = {}) => {
    const url = typeof input === 'string' ? input : input.url;
    const method = (init.method ?? 'GET').toUpperCase();

    const base = [...tokens.keys()].find((b) => url.startsWith(b));
    if (!base || !GUARDED.has(method)) return rawFetch(input, init);

    const token = tokens.get(base);
    const headers = new Headers(init.headers ?? {});
    headers.set(HEADER, token);

    // Appended to whatever session cookie the caller is already sending,
    // exactly as a browser would send both.
    const existing = headers.get('cookie');
    headers.set('cookie', [existing, `${COOKIE}=${token}`].filter(Boolean).join('; '));

    return rawFetch(input, { ...init, headers });
  };

  return tokens;
}
