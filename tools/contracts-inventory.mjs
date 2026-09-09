// Regenerates the inventory table at the bottom of CONTRACTS.md.
//
// It reads the two sides that actually decide the contract — the route
// mappings in AutoPartsHub.Api/Endpoints, and the HttpClient calls in the
// Angular storefront — and joins them on verb plus path. Nothing is inferred:
// a row exists because a line of source exists, and the file and line are
// printed so the claim can be checked.
//
//   node tools/contracts-inventory.mjs [--write] [--web <path-to-web>]
//
// Without --write it prints the table and says nothing else, which is what
// makes it usable as a check: regenerate, diff against the file, and a route
// that moved or lost its caller shows up.
//
// The storefront lives in a different repository. --web points at its `web`
// directory; the default is the sibling checkout this port is developed
// against.

import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';

const args = process.argv.slice(2);
const write = args.includes('--write');
const webArg = args.indexOf('--web');
const API_DIR = resolve(import.meta.dirname, '..', 'AutoPartsHub.Api');
const WEB_DIR = resolve(
  webArg === -1 ? '../autoparts-hub/web' : args[webArg + 1]
);
const CONTRACTS = resolve(import.meta.dirname, '..', 'CONTRACTS.md');

/** Every file under `dir` whose name passes `keep`, depth-first. */
function walk(dir, keep) {
  const out = [];
  const stack = [dir];
  while (stack.length) {
    const here = stack.pop();
    let entries;
    try {
      entries = readdirSync(here);
    } catch {
      continue; // a directory we cannot read is not a contract
    }
    for (const entry of entries) {
      // node_modules holds other people's HttpClient calls, and obj/bin hold
      // generated copies of ours. Both would produce rows that are not the
      // contract.
      if (entry === 'node_modules' || entry === 'obj' || entry === 'bin') continue;
      const path = join(here, entry);
      if (statSync(path).isDirectory()) stack.push(path);
      else if (keep(entry)) out.push(path);
    }
  }
  return out;
}

/** How many lines precede `index`. 1-based, so it reads as an editor does. */
const lineAt = (text, index) => 1 + (text.slice(0, index).match(/\n/g)?.length ?? 0);

/**
 * `{id}`, `{slug}` and `${productId}` are the same thing to a router and
 * different text to a string compare, so matching happens on a form where
 * every placeholder is the same. The real spelling is kept for display.
 */
const normalise = (path) => path.replace(/\{[^}]*\}/g, '{}');

// ---------------------------------------------------------------- the API

const api = new Map(); // "VERB /normalised" -> { path, source }

for (const file of walk(API_DIR, (name) => name.endsWith('.cs'))) {
  const text = readFileSync(file, 'utf8');
  const source = relative(API_DIR, file).replaceAll('\\', '/');
  for (const m of text.matchAll(/Map(Get|Post|Patch|Put|Delete)\("([^"]*)"/g)) {
    const verb = m[1].toUpperCase();
    const path = m[2];
    api.set(`${verb} ${normalise(path)}`, {
      path,
      source: `${source}:${lineAt(text, m.index)}`,
    });
  }
}

// --------------------------------------------------------- the storefront

const callers = new Map(); // "VERB /normalised" -> [source, ...]

for (const file of walk(
  WEB_DIR,
  (name) => name.endsWith('.ts') && !name.endsWith('.spec.ts')
)) {
  const text = readFileSync(file, 'utf8');
  // Relative to `web/src/app`, which is where every caller lives and what
  // makes the column narrow enough to read.
  const source = relative(join(WEB_DIR, 'src', 'app'), file).replaceAll('\\', '/');
  // `this.http\n  .put<T>(...)` is as common in this codebase as `http.put`,
  // so whitespace around the dot is allowed. The generic argument is skipped
  // rather than parsed: it can contain braces, quotes and nested generics,
  // and none of that is the URL.
  for (const m of text.matchAll(/http\s*\.\s*(get|post|put|patch|delete)\s*</g)) {
    const verb = m[1].toUpperCase();
    // The URL is the first string literal after the call opens. 400 characters
    // is past the longest generic in the storefront and short of the next call.
    const tail = text.slice(m.index, m.index + 400);
    const url = tail.match(/[`'"](\/api\/[^`'"]*)/);
    if (!url) continue;
    // The line of the URL itself, not of `this.http`. A call written across
    // four lines is worth citing at the one that names the route.
    const line = lineAt(text, m.index + url.index);

    let path = url[1]
      // A `${id}` in the path is a parameter.
      .replace(/\$\{[^{}]*\}/g, '{}')
      // One holding a query string is not part of the path — the vehicle
      // finder builds `/api/vehicles/find${search ? `?${search}` : ''}`. Cut
      // at whatever is left rather than reading a template as a route.
      .replace(/[$?].*$/, '')
      .replace(/\/$/, '');

    const key = `${verb} ${normalise(path)}`;
    if (!callers.has(key)) callers.set(key, []);
    callers.get(key).push(`${source}:${line}`);
  }
}

// ---------------------------------------------------------------- the join

const rows = [];
for (const key of new Set([...api.keys(), ...callers.keys()])) {
  const [verb, normalised] = [key.slice(0, key.indexOf(' ')), key.slice(key.indexOf(' ') + 1)];
  // The dev probes and the health checks are not contract: nothing in the
  // storefront calls them and they are allowed to change without notice.
  if (normalised.startsWith('/dev/') || normalised.startsWith('/health')) continue;

  const handler = api.get(key);
  const called = callers.get(key);
  rows.push({
    verb,
    path: handler?.path ?? normalised,
    handler: handler ? handler.source : '**absent**',
    callers: called ? [...new Set(called)].sort().join('<br>') : '_none_',
  });
}

rows.sort((a, b) => a.path.localeCompare(b.path) || a.verb.localeCompare(b.verb));

const table = rows
  .map((r) => `| \`${r.verb}\` | \`${r.path}\` | ${r.handler} | ${r.callers} |`)
  .join('\n');

if (!write) {
  process.stdout.write(table + '\n');
} else {
  const doc = readFileSync(CONTRACTS, 'utf8');
  // Everything from the table's header row to the end of the file is
  // generated; the prose above it is not.
  const header = '| Verb | Path | Handler | Called from |\n|---|---|---|---|\n';
  const at = doc.indexOf(header);
  if (at === -1) throw new Error('CONTRACTS.md has no inventory table to replace.');
  writeFileSync(CONTRACTS, doc.slice(0, at + header.length) + table + '\n');
  const absent = rows.filter((r) => r.handler === '**absent**').length;
  const uncalled = rows.filter((r) => r.callers === '_none_').length;
  console.log(
    `${rows.length} routes — ${absent} called and unanswered, ${uncalled} answered and uncalled.`
  );
}
