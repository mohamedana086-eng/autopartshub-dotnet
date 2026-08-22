// Records what the search answers today, so a rewrite can be diffed against it.
//
// HOW TO RUN
// ----------
//   node tools/search-snapshot.mjs save     before changing anything
//   node tools/search-snapshot.mjs check    after
//
// compare.mjs checks the two APIs against each other, which is exactly the
// wrong instrument for changing both: they would agree perfectly on a new
// answer. This checks one API against its own past.
//
// Prices come from the caller's tier, so every case runs as all three
// accounts — a rewrite that quietly loses the pricing context would otherwise
// pass as anonymous and be wrong for everybody signed in.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { installCsrf } from './csrf.mjs';

const BASE = process.env.SNAPSHOT_BASE ?? 'http://localhost:3000';
const FILE = new URL('./search-snapshot.json', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');

await installCsrf(BASE);

const ACCOUNTS = {
  anonymous: null,
  retail: { email: 'walk-in@example.com', password: 'retail123' },
  admin: { email: 'admin@autopartshub.com', password: 'admin123' },
};

const cookies = {};
for (const [name, creds] of Object.entries(ACCOUNTS)) {
  if (!creds) { cookies[name] = ''; continue; }
  const res = await fetch(`${BASE}/api/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(creds),
  });
  cookies[name] = res.headers.getSetCookie().map((c) => c.split(';')[0]).join('; ');
}

/** Every shape of search the harness already compares, plus the paging edges. */
const QUERIES = [
  '', 'q=brake', 'q=brembo', 'q=bosch brake pad', 'q=0986424815', 'q=0 986 424 815',
  'q=P 06 020', 'q=brak pd', 'q=zzzznothing', 'q=thermostat',
  'system=brake-system', 'system=cooling-system&q=thermostat',
  'manufacturer=BOSCH', 'manufacturer=bosch',
  'sort=price-asc', 'sort=price-desc', 'sort=delivery', 'sort=nonsense',
  'minRating=4', 'minRating=9', 'reliability=official', 'reliability=nope', 'returns=true',
  'limit=3', 'limit=0', 'limit=9999',
  'minPrice=20&maxPrice=60', 'minPrice=-5',
  'q=brake&matchIn=part-number', 'q=brake&matchIn=oem,aftermarket', 'q=brake&matchIn=all',
  'supplier=ib16-parts', 'supplier=nope',
  'q=brake&system=brake-system&manufacturer=BOSCH&sort=price-asc',
  'q=0986&matchIn=part-number&minRating=3&returns=true',
  'partType=oem', 'partType=aftermarket', 'partType=substitute',
  'partType=oem,aftermarket', 'partType=aftermarket,oem', 'partType=nonsense',
  'partType=oem,nonsense', 'partType=', 'system=brake-system&partType=oem',
  'q=34 11 6 794 917&matchIn=oem', 'q=34 11 6 794 917&partType=oem',
  'q=34 11 6 794 917&partType=aftermarket',
  'page=2', 'page=2&pageSize=10', 'page=99', 'pageSize=100000', 'pageSize=0',
  'page=0', 'page=-3', 'page=2.7', 'pageSize=7.9', 'page=abc', 'pageSize=abc',
  'limit=3&pageSize=25', 'q=brake&page=2&pageSize=5',
  'q=brake&page=2&pageSize=5&sort=price-asc', 'system=brake-system&page=2&pageSize=3',
  // Combinations the rewrite has to keep straight: a facet count is taken
  // before its own filter but after the ones above it, and getting the order
  // wrong changes numbers that nothing else would catch.
  'system=brake-system&manufacturer=BOSCH',
  'system=brake-system&minRating=5',
  'manufacturer=BOSCH&partType=aftermarket&returns=true',
  'minRating=4&reliability=official&partType=aftermarket',
  'q=brake&minPrice=0&maxPrice=1000',
  'q=brake&sort=delivery&pageSize=3&page=2',
  'variant=nope',
  // A misspelling AND a filter. The fuzzy fallback reaches its rows by id,
  // through a query that knows nothing about the filters, so every one of
  // these went unfiltered once the filters moved into SQL — and no case above
  // combined the two, so nothing here noticed.
  'q=brak pd&system=cooling-system', 'q=brak pd&partType=oem',
  'q=brak pd&manufacturer=BOSCH', 'q=brak pd&minRating=5',
  'q=brak pd&reliability=official', 'q=brak pd&returns=true',
  'q=brak pd&system=brake-system&page=2&pageSize=3',
];

async function capture() {
  const out = {};
  for (const qs of QUERIES) {
    for (const who of Object.keys(ACCOUNTS)) {
      const key = `${who} ${qs}`;
      const headers = cookies[who] ? { cookie: cookies[who] } : {};
      const res = await fetch(`${BASE}/api/catalog/search?${qs}`, { headers });
      out[key] = await res.json();
    }
  }
  return out;
}

function firstDifference(a, b, path = '') {
  if (JSON.stringify(a) === JSON.stringify(b)) return null;

  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) return `${path}.length  before ${a.length}  after ${b.length}`;
    for (let i = 0; i < a.length; i++) {
      const d = firstDifference(a[i], b[i], `${path}[${i}]`);
      if (d) return d;
    }
    return null;
  }

  if (a && b && typeof a === 'object' && typeof b === 'object') {
    const ka = Object.keys(a), kb = Object.keys(b);
    if (ka.join() !== kb.join()) {
      return `${path} keys differ\n      before ${ka.join(', ')}\n      after  ${kb.join(', ')}`;
    }
    for (const k of ka) {
      const d = firstDifference(a[k], b[k], `${path}.${k}`);
      if (d) return d;
    }
    return null;
  }

  return `${path}\n      before ${JSON.stringify(a)}\n      after  ${JSON.stringify(b)}`;
}

const mode = process.argv[2];

if (mode === 'save') {
  const snapshot = await capture();
  mkdirSync(dirname(FILE), { recursive: true });
  writeFileSync(FILE, JSON.stringify(snapshot, null, 1));
  console.log(`saved ${Object.keys(snapshot).length} responses to ${FILE}`);
} else if (mode === 'check') {
  const before = JSON.parse(readFileSync(FILE, 'utf8'));
  const after = await capture();

  let same = 0;
  const keys = Object.keys(before);
  for (const key of keys) {
    const diff = firstDifference(before[key], after[key]);
    if (!diff) { same++; continue; }
    console.log(`DIFF  ${key}`);
    console.log(`   at ${diff}`);
  }
  console.log(`\n${same}/${keys.length} unchanged`);
  if (same !== keys.length) process.exitCode = 1;
} else {
  console.error('usage: node tools/search-snapshot.mjs save|check');
  process.exitCode = 2;
}
