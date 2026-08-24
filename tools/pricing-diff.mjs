// HOW TO RUN
// -----------
// This imports the TypeScript engine, so it runs from inside the Node repo:
//
//   cp tools/pricing-diff.mjs <node-repo>/autoparts-hub/pricing-diff.mts
//   cd <node-repo>/autoparts-hub && npx tsx pricing-diff.mts
//
// with the .NET API running in Development on :5080.
//
// Pushes the same inputs through the TypeScript pricing engine and the C# port
// and compares every field of the result.
//
// The engine is pure on both sides, which makes this possible and makes it the
// right test: hand-porting a few examples proves the examples, and the things
// that actually break in a port are rounding half-cases, ties broken the other
// way, and numbers formatted differently inside the sentence a customer reads.
import { resolvePrice } from '@/lib/pricing';
import { specificityOf } from '@/lib/markup-dimensions';

const BASE = 'http://localhost:5080';
const NET = `${BASE}/dev/price`;

/**
 * The cross-site token, fetched the way a browser gets it.
 *
 * tools/csrf.mjs does this for every other script, but this one is copied into
 * the Node repo on its own to reach the TypeScript engine — so it cannot
 * import a sibling and carries its own copy instead. Two dozen lines duplicated
 * beats a harness that only runs where its imports happen to resolve.
 *
 * Kept rather than exempting the endpoint: an exemption has to be something
 * the server can recognise about a request, and everything a server can
 * recognise a forged request can also claim.
 */
const csrf = await (async () => {
  const res = await fetch(`${BASE}/api/systems`);
  const cookie = res.headers.getSetCookie()
    .map((c) => c.split(';')[0])
    .find((c) => c.startsWith('XSRF-TOKEN='));
  return cookie ? cookie.slice('XSRF-TOKEN='.length) : null;
})();

if (!csrf) {
  console.error('the API issued no XSRF-TOKEN cookie — is it running on :5080?');
  process.exit(2);
}

// Deterministic, so a failure can be re-run. Mulberry32.
let seed = 0x9e3779b9;
const rnd = () => {
  seed |= 0; seed = (seed + 0x6d2b79f5) | 0;
  let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
};
const pick = (xs) => xs[Math.floor(rnd() * xs.length)];
const maybe = (v) => (rnd() < 0.5 ? v : null);

const CATEGORIES = ['cat-retail', 'cat-price9', 'cat-trade'];
const SUPPLIERS = ['sup-1', 'sup-2', 'sup-3'];
const BRANDS = ['BOSCH', 'brembo', 'ATE', 'MAHLE'];
const SYSTEMS = ['brakes', 'cooling-system', 'filter'];
const PREFIXES = ['BP', 'bp-', '0 986', 'ZZ'];
const GOODS = ['consumables', 'heavy', 'slow-moving'];
const TYPES = ['PERCENT', 'AMOUNT', 'FIXED', 'PERCENT_MIN'];

// Around the seasons, so a window sometimes covers "now" and sometimes does
// not, and sometimes has only one bound. Fixed instants rather than offsets
// from the real clock, so a failure can be re-run tomorrow and still fail.
const JUNE = Date.UTC(2026, 5, 1);
const JULY = Date.UTC(2026, 6, 1);
const AUGUST = Date.UTC(2026, 7, 1);
const MOMENTS = [JUNE, JULY, AUGUST];
const PART_TYPES = ['oem', 'aftermarket', 'substitute'];
const NAMES = ['Brake pad set, front', 'Oil filter', 'Radiator hose', 'CLUTCH KIT'];
const WORDS = ['brake', 'FILTER', 'hose', 'pad set', 'zz'];
const CLIENTS = ['cli-1', 'cli-2', 'cli-3'];
const ROLES = ['RETAIL', 'B2B', 'SALES'];
const MANAGERS = ['mgr-1', 'mgr-2'];
const CITIES = ['Cairo', 'ALEXANDRIA', 'tanta'];
const LISTS = ['pl-a', 'pl-b'];

/**
 * Every dimension a rule can narrow on, and values to draw from.
 *
 * The last entry is deliberately not a dimension either build knows. A rule
 * carrying one has to stop applying on BOTH ports rather than applying on one
 * of them — a rule written by a newer version of the software must not price
 * differently depending on which API answered.
 */
const DIMENSIONS = [
  ['supplier', SUPPLIERS],
  ['manufacturer', BRANDS],
  ['vehicleSystem', SYSTEMS],
  ['goodsCategory', GOODS],
  ['partType', PART_TYPES],
  ['partNumberPrefix', PREFIXES],
  ['nameContains', WORDS],
  ['clientCategory', CATEGORIES],
  ['client', CLIENTS],
  ['clientRole', ROLES],
  ['salesManager', MANAGERS],
  ['city', CITIES],
  ['currency', ['EUR', 'USD', 'EGP', 'GBP']],
  ['priceList', LISTS],
  ['deliveryTerms', ['ex-works', 'delivered']],
];
const CURRENCIES = [
  null,
  { code: 'EUR', symbol: '€', rate: 1 },
  { code: 'USD', symbol: '$', rate: 1.0873 },
  { code: 'EGP', symbol: 'E£', rate: 48.37 },
];

const makeCtx = () => ({
  basePrice: pick([0, 0.01, 1, 8.4, 33.905, 42.75, 118.5, 1999.99]),
  supplierId: pick(SUPPLIERS),
  manufacturerName: pick(BRANDS),
  vehicleSystemSlug: pick(SYSTEMS),
  partNumber: pick(['BP-1234', 'bp-9', '0 986 424 815', 'ACP 34 000S']),
  clientCategoryId: pick(CATEGORIES),
  clientCategoryMarkupPercent: pick([0, 4, 23, 50, 65.65, 100]),
  // The goods-category rung. Generated as a pair so all four states occur:
  // no category at all, a category with no markup, and a category whose
  // markup either does or does not have a rule above it. The ordering of the
  // ladder is the part a port gets wrong, and it only shows when the rungs
  // disagree — which is why the values below never coincide with the client
  // category defaults above.
  goodsCategoryId: maybe(pick(GOODS)) ?? undefined,
  goodsCategoryMarkup: rnd() < 0.5
    ? undefined
    : (() => {
        const type = pick(TYPES);
        return {
          label: pick(['Consumables', 'Heavy parts', 'Slow-moving']),
          type,
          value: pick([7, 42, 78, 95, 300, -50]),
          // The floor belongs to exactly one type, the same way the database
          // and both validators have it.
          minAmount: type === 'PERCENT_MIN' ? pick([0, 0.5, 2, 25]) : null,
        };
      })(),
  discountPercent: pick([undefined, 0, 7.5, 10, 33.333, 100, -5, 150]),
  currency: pick(CURRENCIES) ?? undefined,
  // Always set, so a windowed rule is asked about a definite moment rather
  // than about whenever each port happened to read its clock.
  now: pick(MOMENTS),
  // The caller-side dimensions, and the two part-side ones added with them.
  // Undefined as often as set, because "the request cannot answer" is a state
  // the two ports have to agree about as much as any value is.
  partName: maybe(pick(NAMES)) ?? undefined,
  partType: maybe(pick(PART_TYPES)) ?? undefined,
  clientId: maybe(pick(CLIENTS)) ?? undefined,
  clientRole: maybe(pick(ROLES)) ?? undefined,
  salesManagerId: maybe(pick(MANAGERS)) ?? undefined,
  city: maybe(pick(CITIES)) ?? undefined,
  priceListId: maybe(pick(LISTS)) ?? undefined,
});

/**
 * A rule's conditions: up to three dimensions, each holding up to three values.
 *
 * Lists of more than one are the point. A rule naming three suppliers has to
 * match any of them and still rank where a rule naming one ranks, and a port
 * that reads the rows flat would instead require all three at once — which
 * nothing satisfies, so it would quietly never apply.
 */
const makeConditions = () => {
  const chosen = new Set();
  const conditions = [];

  for (let i = 0, wanted = Math.floor(rnd() * 4); i < wanted; i++) {
    const [name, values] = pick(DIMENSIONS);
    if (chosen.has(name)) continue;
    chosen.add(name);

    // The group points one way or the other, never both — which is what the
    // write path enforces, so it is what the engine should be compared on.
    // An exclusion is a different question from a longer list, and the two
    // ports have to answer it the same way.
    const negated = rnd() < 0.3;

    const many = new Set();
    for (let j = 0, count = 1 + Math.floor(rnd() * 3); j < count; j++) {
      many.add(pick(values));
    }
    for (const value of many) conditions.push({ dimension: name, value, negated });
  }

  return conditions;
};

/** A window: neither bound, one of them, or both — and sometimes backwards. */
const makeWindow = () => {
  const roll = rnd();
  if (roll < 0.55) return { startsAtMs: null, endsAtMs: null };
  if (roll < 0.7) return { startsAtMs: pick(MOMENTS), endsAtMs: null };
  if (roll < 0.85) return { startsAtMs: null, endsAtMs: pick(MOMENTS) };
  return { startsAtMs: JUNE, endsAtMs: pick([JUNE, JULY, AUGUST]) };
};

const makeRule = (i) => {
  const conditions = makeConditions();
  const from = maybe(pick([0, 10, 50]));
  const to = maybe(pick([10, 100, 5000]));
  const type = pick(TYPES);
  const window = makeWindow();

  return {
    id: `r${i}`,
    label: `Rule ${i}`,
    priority: pick([-1, 0, 3, 5, 10]),
    conditions,
    // Computed once and sent to both, the way a save computes it once and
    // stores it. The .NET probe recomputes it from the conditions it receives
    // rather than trusting this number, so the two implementations of the
    // arithmetic are compared here too.
    specificity: specificityOf(conditions, from !== null || to !== null),
    purchasePriceFrom: from,
    purchasePriceTo: to,
    type,
    value: pick([0, 2, 12, 18, 26, 99.99]),
    minAmount: type === 'PERCENT_MIN' ? pick([0, 0.5, 2, 25]) : null,
    ...window,
    active: rnd() < 0.85,
  };
};

// The C# side takes the enum by name and the JSON is camelCased both ways.
/** The C# enum takes its members by name; the JSON is camelCased both ways. */
const NET_TYPE = {
  PERCENT: 'Percent', AMOUNT: 'Amount', FIXED: 'Fixed', PERCENT_MIN: 'PercentMin',
};

const forNet = (ctx, rules) => ({
  context: {
    basePrice: ctx.basePrice,
    supplierId: ctx.supplierId,
    manufacturerName: ctx.manufacturerName,
    vehicleSystemSlug: ctx.vehicleSystemSlug,
    partNumber: ctx.partNumber,
    clientCategoryId: ctx.clientCategoryId,
    clientCategoryMarkupPercent: ctx.clientCategoryMarkupPercent,
    discountPercent: ctx.discountPercent ?? null,
    currency: ctx.currency ?? null,
    goodsCategoryId: ctx.goodsCategoryId ?? null,
    partName: ctx.partName ?? null,
    partType: ctx.partType ?? null,
    clientId: ctx.clientId ?? null,
    clientRole: ctx.clientRole ?? null,
    salesManagerId: ctx.salesManagerId ?? null,
    city: ctx.city ?? null,
    priceListId: ctx.priceListId ?? null,
    nowMs: ctx.now,
    goodsCategoryMarkup: ctx.goodsCategoryMarkup
      ? {
          ...ctx.goodsCategoryMarkup,
          type: NET_TYPE[ctx.goodsCategoryMarkup.type],
        }
      : null,
  },
  rules: rules.map((r) => ({ ...r, type: NET_TYPE[r.type] })),
});

const FIELDS = [
  'basePrice', 'finalPrice', 'netBase', 'appliedRule', 'marginPercent',
  'discountPercent', 'priceBeforeDiscount', 'currencyCode', 'currencySymbol',
];

let checked = 0;
const failures = [];

for (let i = 0; i < 400; i++) {
  const ctx = makeCtx();
  const rules = Array.from({ length: Math.floor(rnd() * 5) }, (_, n) => makeRule(n));

  const ts = resolvePrice(ctx, rules);
  const res = await fetch(NET, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      // Both halves, exactly as a browser sends them: the server set the
      // cookie, and the page is expected to copy it into the header.
      'X-XSRF-TOKEN': csrf,
      cookie: `XSRF-TOKEN=${csrf}`,
    },
    body: JSON.stringify(forNet(ctx, rules)),
  });
  if (!res.ok) {
    failures.push({ i, note: `HTTP ${res.status}`, body: (await res.text()).slice(0, 200) });
    continue;
  }
  const net = await res.json();
  checked++;

  for (const f of FIELDS) {
    const a = ts[f];
    const b = net[f];
    const same = typeof a === 'number' && typeof b === 'number'
      ? Math.abs(a - b) < 1e-9
      : a === b;
    if (!same) {
      failures.push({ i, field: f, ts: a, net: b, ctx, rules });
      break;
    }
  }
}

console.log(`${checked} cases run, ${failures.length} mismatch(es)\n`);

for (const f of failures.slice(0, 4)) {
  if (f.note) { console.log(`  case ${f.i}: ${f.note} ${f.body}`); continue; }
  console.log(`  case ${f.i}: ${f.field}  ts=${JSON.stringify(f.ts)}  net=${JSON.stringify(f.net)}`);
  console.log(`    base=${f.ctx.basePrice} tierMarkup=${f.ctx.clientCategoryMarkupPercent} discount=${f.ctx.discountPercent} ccy=${f.ctx.currency?.code ?? 'base'}`);
  console.log(`    rules: ${f.rules.length}`);
}

if (failures.length > 0) process.exitCode = 1;
