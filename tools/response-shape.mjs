// Do the two APIs still build the same response, key for key and in order?
//
//   node tools/response-shape.mjs
//
// WHY THIS EXISTS
// ---------------
// tools/compare.mjs is the real answer to that question — it sends 325
// requests to both APIs and diffs the bodies. Its own header states the
// premise it rests on: "Both read the same database, so the same request can
// be asked of each."
//
// That premise is gone. The .NET API's raw SQL was ported to SQL Server in
// place, so it no longer runs against PostgreSQL, and the two are pointed at
// different databases holding different rows. compare.mjs cannot run again
// until the cutover copies the data across, and every response change made
// between now and then is made without it.
//
// So this checks the one thing that can still be checked from the source
// alone: that both files build the same object, with the same keys, in the
// same order. Order matters because compare.mjs reports a difference by
// joining the key list — `{a,b}` and `{b,a}` are a failure there — and because
// getting it right now is what makes compare.mjs green on the day it can run
// again rather than red for a reason nobody remembers.
//
// WHAT IT DOES NOT CHECK
// ----------------------
// Values. A key present in both and holding a different thing is exactly what
// compare.mjs is for, and nothing here replaces it. This narrows the gap; it
// does not close it.

import { readFileSync } from 'node:fs';

const NODE = process.env.NODE_REPO
  ?? 'C:/Users/Aio/autoparts-hub/autoparts-hub';

const NET = process.env.NET_REPO ?? 'C:/Users/Aio/autopartshub-dotnet';

/**
 * The responses to compare: where each API builds one, and what to call it.
 *
 * Anchored on the text that opens the literal rather than on a line number,
 * so an edit above it does not silently point this at something else.
 */
const RESPONSES = [
  {
    what: 'GET /api/catalog/search',
    node: {
      file: `${NODE}/app/api/catalog/search/route.ts`,
      // Stops before the brace, like the C# anchor below: the scan has to see
      // the object open or it counts every top-level key as being outside it.
      opens: 'return NextResponse.json(',
    },
    net: {
      file: `${NET}/AutoPartsHub.Api/Endpoints/SearchEndpoints.cs`,
      opens: 'return Results.Ok(new',
    },
    // Out of reach of a static read, with the reason.
    //
    // The .NET rows are a record — SearchProductWithSpecsDto — declared far
    // from the response and assembled by a constructor, so its fields are not
    // in the literal to be counted. The Node rows are an inline object, so
    // theirs are. Comparing the two would report every field of one against
    // none of the other.
    //
    // Named rather than filtered silently: an exclusion nobody can see is how
    // a check comes to pass by looking at less and less.
    except: ['products.'],
  },
];

/** Strips // line comments, /* block *​/ comments and string literals. */
function bare(source) {
  let out = '';
  let i = 0;

  while (i < source.length) {
    const two = source.slice(i, i + 2);

    if (two === '//') {
      while (i < source.length && source[i] !== '\n') i++;
      continue;
    }
    if (two === '/*') {
      i += 2;
      while (i < source.length && source.slice(i, i + 2) !== '*/') i++;
      i += 2;
      continue;
    }
    // A string, in either language. Replaced by a space rather than removed,
    // so nothing on either side of it runs together into a false identifier.
    if (source[i] === '"' || source[i] === "'" || source[i] === '`') {
      const quote = source[i++];
      while (i < source.length && source[i] !== quote) {
        if (source[i] === '\\') i++;
        i++;
      }
      i++;
      out += ' ';
      continue;
    }

    out += source[i++];
  }

  return out;
}

/**
 * The keys of one object literal, in order, with nested objects named.
 *
 * Depth is counted in braces from the opening one. A key at depth 1 is a
 * top-level field; a key inside a nested object is reported as `parent.key`,
 * which is how the difference reads when a facet moves rather than a field.
 */
function keysOf(source, opens) {
  const at = source.indexOf(opens);
  if (at === -1) throw new Error(`could not find ${JSON.stringify(opens)}`);

  const text = bare(source.slice(at + opens.length));
  const keys = [];
  const path = [];

  let depth = 0;
  let i = 0;
  // The identifier most recently seen at this depth, which becomes the parent
  // name when a `{` follows it.
  let lastName = null;
  // The last character that was not whitespace. A key only ever follows the
  // brace that opened its object or the comma after its neighbour — which is
  // what tells `count: x` apart from the `:` of a ternary, and is far steadier
  // than a list of keywords to exclude.
  let previous = '';

  while (i < text.length) {
    const c = text[i];

    if (c === '{') {
      depth++;
      if (depth > 1 && lastName) path.push(lastName);
      lastName = null;
      previous = c;
      i++;
      continue;
    }
    if (c === '}') {
      depth--;
      if (depth < 1) break;
      path.pop();
      previous = c;
      i++;
      continue;
    }

    // An identifier followed by `:` (TypeScript) or `=` (C#) is a key. `==` is
    // not, and neither is `=>`.
    const name = /^([A-Za-z_]\w*)\s*([:=])([^=>])/.exec(text.slice(i));
    if (name && depth >= 1 && (previous === '{' || previous === ',')) {
      const [, key, , after] = name;
      keys.push([...path, key].join('.'));
      lastName = key;
      previous = key;
      i += name[0].length - after.length;
      continue;
    }

    // Shorthand, which BOTH languages have and which is most of what these two
    // responses are made of: `new { system, supplier }` in C# and
    // `{ name, count }` in TypeScript both name a field after the value going
    // into it. Missing it reported half the response as a difference.
    const shorthand = /^([A-Za-z_]\w*)\s*([,}])/.exec(text.slice(i));
    if (shorthand && depth >= 1 && (previous === '{' || previous === ',')) {
      const [, key] = shorthand;
      keys.push([...path, key].join('.'));
      lastName = key;
      previous = key;
      i += key.length;
      continue;
    }

    if (!/\s/.test(c)) previous = c;
    i++;
  }

  return keys;
}

let failures = 0;

for (const response of RESPONSES) {
  console.log(`\n${response.what}`);

  const except = response.except ?? [];
  const compared = (keys) => keys.filter((k) => !except.some((prefix) => k.startsWith(prefix)));

  const node = compared(keysOf(readFileSync(response.node.file, 'utf8'), response.node.opens));
  const net = compared(keysOf(readFileSync(response.net.file, 'utf8'), response.net.opens));

  for (const prefix of except) console.log(`  --    ${prefix}* not compared — see the note`);

  if (node.join('|') === net.join('|')) {
    console.log(`  ok    ${node.length} keys, same order in both`);
    continue;
  }

  failures++;

  const onlyNode = node.filter((k) => !net.includes(k));
  const onlyNet = net.filter((k) => !node.includes(k));

  if (onlyNode.length > 0) console.log(`  FAIL  only in node:    ${onlyNode.join(', ')}`);
  if (onlyNet.length > 0) console.log(`  FAIL  only in dotnet:  ${onlyNet.join(', ')}`);

  if (onlyNode.length === 0 && onlyNet.length === 0) {
    // The same keys in a different order. compare.mjs reports this as a
    // difference, so it is one.
    const first = node.findIndex((k, at) => net[at] !== k);
    console.log(`  FAIL  same keys, different order — first at ${first}: `
      + `node ${node[first]}, dotnet ${net[first]}`);
  }
}

console.log(failures === 0
  ? '\nthe responses are built the same way.'
  : `\n${failures} response(s) differ.`);

process.exit(failures === 0 ? 0 : 1);
