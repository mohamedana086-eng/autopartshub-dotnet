// Rewrites the PostgreSQL dialect in this application's raw SQL to T-SQL.
//
//   node tools/sql-port.mjs <pass> [--dry]
//
// Passes, in the order they are meant to run:
//   casts      ::text, ::int and friends on parameters and aggregates
//   booleans   true/false literals, ON true, IS TRUE, bare boolean columns
//   ilike      ILIKE -> LIKE
//   concat     || -> +
//
// WHY THIS IS A TOOL AND NOT A SED COMMAND
// ----------------------------------------
// The first attempt was a sed command, and it rewrote the words "and IS TRUE"
// inside a SQL comment into "(and = 1)". Every statement in this codebase is
// commented, often heavily, and the comments talk about the SQL — so they are
// full of the exact phrases being rewritten.
//
// So: transformation happens on code only. Each line is split at its `--`,
// the code half is rewritten, the comment half is returned untouched, and the
// two are rejoined. A `--` inside a string literal is not a comment, which is
// what the scan below is careful about.
//
// Everything happens inside $"""...""" blocks, so nothing in the surrounding
// C# is touched either.
//
// AND WHY IT CHECKS ITS OWN WORK
// ------------------------------
// The second thing the sed command did was leave a statement with one more
// `(` than `)`. Unbalanced parentheses are the failure mode of every rewrite
// here that wraps an expression, so each block is counted before and after and
// a change in the balance is refused rather than written.

import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';

const API = resolve(import.meta.dirname, '..', 'AutoPartsHub.Api');
const pass = process.argv[2];
const dry = process.argv.includes('--dry');

if (!pass) {
  console.error('usage: node tools/sql-port.mjs <casts|booleans|ilike|concat> [--dry]');
  process.exit(1);
}

function walk(dir) {
  const out = [];
  const stack = [dir];
  while (stack.length) {
    const here = stack.pop();
    for (const entry of readdirSync(here)) {
      if (entry === 'obj' || entry === 'bin') continue;
      const path = join(here, entry);
      if (statSync(path).isDirectory()) stack.push(path);
      else if (entry.endsWith('.cs')) out.push(path);
    }
  }
  return out;
}

/**
 * Where the code ends and the comment begins on one line.
 *
 * A `--` inside a string literal is two hyphens, not a comment: the part
 * numbers and the LIKE patterns in these statements both contain them.
 * Returns the line length when there is no comment.
 */
function commentStart(line) {
  let quoted = false;
  for (let i = 0; i < line.length - 1; i++) {
    const c = line[i];
    if (c === "'") {
      // '' inside a literal is an escaped quote, and skipping it keeps the
      // rest of the line on the right side of the fence.
      if (quoted && line[i + 1] === "'") i++;
      else quoted = !quoted;
    } else if (!quoted && c === '-' && line[i + 1] === '-') {
      return i;
    }
  }
  return line.length;
}

const balance = (s) => [...s].reduce((n, c) => n + (c === '(' ? 1 : c === ')' ? -1 : 0), 0);

const rewrites = {
  casts: (sql) =>
    sql
      // Npgsql needed a type on an interpolated parameter, and on an aggregate
      // whose PostgreSQL type is bigint. SqlClient types its own parameters,
      // and SQL Server's COUNT is already int, so both annotations simply go.
      // Array casts are left alone: they need OPENJSON, not deletion.
      .replace(/::(?:boolean|bool|timestamp|text|int)\b(?!\s*\[\])/g, ''),

  booleans: (sql) =>
    sql
      // A LATERAL join's `ON true` is a join condition, not a value. SQL Server
      // wants a predicate there and `ON 1` is not one, so this runs before the
      // literal rewrite below — which would otherwise produce exactly that.
      .replace(/\bON\s+true\b/gi, 'ON 1 = 1')
      // `x IS TRUE` is PostgreSQL's null-safe test: false when x is null, where
      // `x = true` would be null. SQL Server has neither form, and the
      // difference is the whole reason the original was written this way.
      //
      // The subject is [^\s()]+ and not \S+ on purpose. `\S+` is greedy and a
      // parenthesis is not whitespace, so in `AND ({flag} IS NOT TRUE OR …)`
      // it captures `({flag}` — bracket included — and the replacement then
      // emits that bracket twice. The balance check below caught it; this is
      // why it no longer needs to.
      .replace(/([^\s()]+)\s+IS\s+NOT\s+TRUE\b/gi, '($1 IS NULL OR $1 = 0)')
      .replace(/([^\s()]+)\s+IS\s+TRUE\b/gi, '($1 = 1)')
      // A bit is not a condition.
      .replace(/=\s*true\b/gi, '= 1')
      .replace(/=\s*false\b/gi, '= 0')
      // Bare boolean columns standing as conditions — `WHERE s."active"`.
      .replace(
        /\b(AND|OR|WHERE)(\s+)([a-z]+\."(?:active|isBase|narrowed|internal|fromStaff|exactMatch|isOEM|acceptsReturns|weightComplete)")(?!\s*(?:=|<>|!=|IS)\b)/g,
        '$1$2$3 = 1'
      ),

  // The default SQL Server collation is case-insensitive, so LIKE already
  // means what ILIKE meant here.
  ilike: (sql) => sql.replace(/\bILIKE\b/g, 'LIKE'),

  // `||` does not concatenate in T-SQL.
  concat: (sql) => sql.replace(/'%'\s*\|\|\s*(\w+)\s*\|\|\s*'%'/g, "'%' + $1 + '%'"),
};

const rewrite = rewrites[pass];
if (!rewrite) {
  console.error(`no such pass: ${pass}`);
  process.exit(1);
}

/** Applies the pass to code, never to comments. */
function transform(sql) {
  return sql
    .split('\n')
    .map((line) => {
      const at = commentStart(line);
      return rewrite(line.slice(0, at)) + line.slice(at);
    })
    .join('\n');
}

let files = 0;
let blocks = 0;
const refused = [];

for (const file of walk(API)) {
  const source = readFileSync(file, 'utf8');
  let touched = false;

  const next = source.replace(/(\$""")([\s\S]*?)(""")/g, (whole, open, sql, close) => {
    const ported = transform(sql);
    if (ported === sql) return whole;

    // A rewrite that wraps an expression is one paren away from changing what
    // the statement means while still parsing. Refuse rather than write.
    if (balance(ported) !== balance(sql)) {
      refused.push(relative(API, file).replaceAll('\\', '/'));
      return whole;
    }

    touched = true;
    blocks++;
    return open + ported + close;
  });

  if (!touched) continue;
  files++;
  if (!dry) writeFileSync(file, next);
}

console.log(
  `${pass}: ${blocks} statements in ${files} files${dry ? ' (dry run)' : ''}` +
    (refused.length ? `\n  REFUSED, parens would change: ${[...new Set(refused)].join(', ')}` : '')
);
