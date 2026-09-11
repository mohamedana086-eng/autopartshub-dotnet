// Rewrites the PostgreSQL dialect in this application's raw SQL to T-SQL.
//
//   node tools/sql-port.mjs <pass> [--dry]
//
// Passes, in the order they are meant to run:
//   casts      ::text, ::int and friends on parameters and aggregates
//   booleans   true/false literals, ON true, IS TRUE, bare boolean columns
//   ilike      ILIKE -> LIKE
//   concat     || -> +
//   lateral    LEFT JOIN LATERAL -> OUTER APPLY, JOIN LATERAL -> CROSS APPLY
//   limit      LIMIT/OFFSET -> OFFSET/FETCH, or TOP where there is no order
//
// WHY THIS IS A TOOL AND NOT A SED COMMAND
// ----------------------------------------
// The first attempt was a sed command, and it rewrote the words "and IS TRUE"
// inside a SQL comment into "(and = 1)". Every statement in this codebase is
// commented, often heavily, and the comments talk about the SQL — so they are
// full of the exact phrases being rewritten.
//
// So: transformation happens on code only, never on comment text, and a `--`
// inside a string literal is two hyphens rather than a comment.
//
// Everything happens inside $"""...""" blocks, so nothing in the surrounding
// C# is touched either.
//
// AND WHY IT CHECKS ITS OWN WORK
// ------------------------------
// The second thing the sed command did was leave a statement with one more
// `(` than `)`. Unbalanced brackets are the failure mode of every rewrite here
// that wraps an expression, so each block is counted before and after and a
// change in the balance is refused rather than written. That check has already
// caught one real bug — see the note on the IS TRUE rewrite.

import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';

const API = resolve(import.meta.dirname, '..', 'AutoPartsHub.Api');
const pass = process.argv[2];
const dry = process.argv.includes('--dry');

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

/**
 * Hides every comment behind a placeholder so a transform can span lines.
 *
 * Rewrites that fit on one line can just be applied to the half of the line
 * before its `--`. A LATERAL join cannot: the join, its body and its
 * `ON 1 = 1` are several lines apart, and matching across them means matching
 * across whatever comments sit in between — which in this codebase is most of
 * the text.
 *
 * `@@C<n>@@` is plain ASCII and does not occur in this SQL. Two earlier
 * choices did not work: a bare number in spaces, which would have matched
 * `LIMIT 1` and `= 0` everywhere, and a NUL byte, which turned the file binary
 * to every tool that reads it. It contains no bracket, so masking cannot
 * disturb the balance check.
 */
function maskComments(sql) {
  const comments = [];
  const masked = sql
    .split('\n')
    .map((line) => {
      const at = commentStart(line);
      if (at === line.length) return line;
      comments.push(line.slice(at));
      return line.slice(0, at) + `@@C${comments.length - 1}@@`;
    })
    .join('\n');
  return { masked, comments };
}

const unmask = (sql, comments) =>
  sql.replace(/@@C(\d+)@@/g, (_, i) => comments[Number(i)]);

// ---------------------------------------------------- line-at-a-time passes

const lineRewrites = {
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
      // bracket is not whitespace, so in `AND ({flag} IS NOT TRUE OR …)` it
      // captures `({flag}` — bracket included — and the replacement emits that
      // bracket twice. The balance check caught it; this is why it no longer
      // has to.
      .replace(/([^\s()]+)\s+IS\s+NOT\s+TRUE\b/gi, '($1 IS NULL OR $1 = 0)')
      .replace(/([^\s()]+)\s+IS\s+TRUE\b/gi, '($1 = 1)')
      // A bit is not a condition.
      .replace(/=\s*true\b/gi, '= 1')
      .replace(/=\s*false\b/gi, '= 0')
      // Bare boolean columns standing as conditions — `WHERE s."active"`.
      //
      // The lookahead is what stops this firing on a column that already has
      // its comparison, and it was wrong the first time: `(?:=|<>|!=|IS)\b`
      // puts a word boundary after `=`, which requires a word character next.
      // `= 1` has a space, so the boundary failed, the alternation failed, the
      // negative lookahead succeeded, and the rule appended a second `= 1` to
      // text it had already fixed. Seven statements went out reading
      // `w."active" = 1 = 1` before the idempotence check below existed.
      //
      // The alias is optional: `WHERE "active"` inside a subquery over one
      // table is the same mistake as `WHERE s."active"`, and requiring the
      // prefix left nine of them behind — found, as usual, by the engine
      // refusing the statement and blaming the line after.
      .replace(
        /\b(AND|OR|WHERE)(\s+)((?:[a-z]+\.)?"(?:active|isBase|narrowed|internal|fromStaff|exactMatch|isOEM|acceptsReturns|weightComplete)")(?!\s*(?:=|<>|!=|\bIS\b))/g,
        '$1$2$3 = 1'
      ),

  // The default SQL Server collation is case-insensitive, so LIKE already
  // means what ILIKE meant here.
  ilike: (sql) => sql.replace(/\bILIKE\b/g, 'LIKE'),

  // `||` does not concatenate in T-SQL.
  concat: (sql) => sql.replace(/'%'\s*\|\|\s*(\w+)\s*\|\|\s*'%'/g, "'%' + $1 + '%'"),
};

// -------------------------------------------------------- whole-block passes

const blockRewrites = {
  /**
   * LATERAL becomes APPLY.
   *
   * The two mean the same thing — a subquery evaluated per outer row, able to
   * see that row's columns. The shapes differ only in that APPLY carries no
   * join condition, so the `ON 1 = 1` that LATERAL needs (and that nothing in
   * PostgreSQL needs either, it is there because the grammar demands a
   * condition) goes with it.
   *
   * LEFT becomes OUTER and plain becomes CROSS, which is the same distinction
   * under a different spelling: keep the outer row when the body returns
   * nothing, or drop it.
   */
  lateral: (sql) =>
    sql
      .replace(
        /\bLEFT\s+JOIN\s+LATERAL\s*\(([\s\S]*?)\)\s*(\w+)\s+ON\s+1\s*=\s*1/g,
        (_, body, alias) => `OUTER APPLY (${body}) ${alias}`
      )
      .replace(
        /\bJOIN\s+LATERAL\s*\(([\s\S]*?)\)\s*(\w+)\s+ON\s+1\s*=\s*1/g,
        (_, body, alias) => `CROSS APPLY (${body}) ${alias}`
      ),

  /**
   * `= ANY(array)` becomes a subquery over OPENJSON.
   *
   * SQL Server has no array parameter, so the list travels as JSON in one
   * nvarchar and is read back as rows. See SqlList for why JSON rather than a
   * delimiter, a splice, or a parameter per value.
   *
   * The negated form is rewritten as NOT EXISTS rather than NOT IN. They are
   * not the same: `x NOT IN (…)` is unknown — and therefore not true — as soon
   * as the list contains a null, so a single null would silently empty the
   * result. NOT EXISTS has no such edge, and both sites here are deletions
   * where "silently matched nothing" would mean deleting nothing.
   *
   * Only the single-array forms. The parallel-array `unnest(a, b, c)` bulk
   * inserts are a different problem — JSON objects rather than JSON arrays —
   * and are left for a pass that can restructure the C# beside them.
   *
   * COLLATE DATABASE_DEFAULT is not decoration. When OPENJSON reads a
   * parameter its `value` column comes back as Latin1_General_BIN2, and
   * comparing that to a column in the database's own collation is an outright
   * error — fourteen statements said so. The quiet half is worse than the
   * loud one: Latin1_General_BIN2 is case-SENSITIVE, so the obvious fix of
   * collating the column instead would have left every one of these lookups
   * matching case-sensitively while every other string comparison in the
   * application does not.
   */
  arrays: (sql) =>
    sql
      .replace(
        /NOT\s*\(\s*([^()]+?)\s*=\s*ANY\(\{([^}]+)\}::text\[\]\)\s*\)/g,
        (_, column, list) =>
          `NOT EXISTS (SELECT 1 FROM OPENJSON({SqlList.Of(${list})}) ` +
          `WHERE value COLLATE DATABASE_DEFAULT = ${column})`
      )
      .replace(
        /=\s*ANY\(\{([^}]+)\}::text\[\]\)/g,
        (_, list) =>
          `IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(${list})}))`
      )
      // The switch-the-filter-off guard in front of one of them.
      .replace(/\{([^}]+)\}::text\[\]\s+IS\s+NULL/g, (_, list) => `{SqlList.Of(${list})} IS NULL`),

  /**
   * Adds the collation to OPENJSON reads written before it was known to be
   * needed. See the note on `arrays` for why it is not optional.
   */
  collation: (sql) =>
    sql
      .replace(
        /SELECT value FROM OPENJSON\(/g,
        'SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON('
      )
      .replace(/WHERE value = /g, 'WHERE value COLLATE DATABASE_DEFAULT = '),

  /**
   * LIMIT becomes OFFSET/FETCH, or TOP.
   *
   * `OFFSET … FETCH` is the standard form and the one that pages, but SQL
   * Server requires an ORDER BY with it. PostgreSQL does not require one with
   * LIMIT, and this codebase has statements both ways, so which form is
   * correct depends on the statement:
   *
   * - With an ORDER BY, OFFSET/FETCH. The order is already decided and the
   *   paging is what the clause is for.
   * - Without one, TOP. `LIMIT 1` with no ORDER BY asks for an arbitrary row,
   *   and TOP asks for exactly the same thing. Inventing an ORDER BY here
   *   would be inventing a decision nobody made — and `ORDER BY (SELECT NULL)`
   *   would be that decision written as noise.
   */
  limit: (sql) => {
    // `LIMIT n OFFSET m` — always paging, and every one of these is ordered.
    sql = sql.replace(
      /\bLIMIT\s+(\{[^}]*\}|\d+)\s+OFFSET\s+(\{[^}]*\}|\d+)/g,
      (_, take, skip) => `OFFSET ${skip} ROWS FETCH NEXT ${take} ROWS ONLY`
    );

    // A bare `LIMIT n`. Ordered statements take FETCH; the rest take TOP,
    // which means finding the SELECT that owns the LIMIT.
    return sql.replace(/\bLIMIT\s+(\{[^}]*\}|\d+)/g, (whole, take, at) => {
      const before = sql.slice(0, at);

      // The ORDER BY belonging to this LIMIT is the last one not closed off by
      // a bracket opened after it. Counting brackets backwards is what tells a
      // subquery's own ORDER BY from an enclosing statement's.
      const order = before.lastIndexOf('ORDER BY');
      if (order !== -1 && balance(before.slice(order)) >= 0) {
        return `OFFSET 0 ROWS FETCH NEXT ${take} ROWS ONLY`;
      }
      return `@@TOP${take}@@`;
    });
  },
};

// A second step for the TOP cases: the marker left above says "this SELECT
// takes a TOP", and the SELECT it belongs to is the nearest one that opened
// and has not been closed.
function applyTopMarkers(sql) {
  return sql.replace(/\s*@@TOP(\{[^}]*\}|\d+)@@/g, (_, take, at) => {
    const before = sql.slice(0, at);
    let depth = 0;
    for (let i = before.length - 1; i >= 0; i--) {
      const c = before[i];
      if (c === ')') depth++;
      else if (c === '(') {
        if (depth === 0) break;
        depth--;
      } else if (depth === 0 && before.slice(i, i + 6).toUpperCase() === 'SELECT') {
        // Found it. The marker is removed and the TOP is inserted there by the
        // caller, which needs the index — so this returns nothing and the
        // insertion happens in the pass below.
        tops.push({ at: i + 6, take });
        return '';
      }
    }
    unplaced.push(take);
    return '';
  });
}

let tops = [];
let unplaced = [];

function limitPass(sql) {
  tops = [];
  unplaced = [];
  const marked = blockRewrites.limit(sql);
  const stripped = applyTopMarkers(marked);
  if (unplaced.length) return null;
  // Inserted back to front so the earlier offsets stay valid.
  let out = stripped;
  for (const { at, take } of tops.sort((a, b) => b.at - a.at)) {
    out = out.slice(0, at) + ` TOP ${take}` + out.slice(at);
  }
  return out;
}

// ------------------------------------------------------------------- driver

function transform(sql) {
  if (lineRewrites[pass]) {
    const rewrite = lineRewrites[pass];
    return sql
      .split('\n')
      .map((line) => {
        const at = commentStart(line);
        return rewrite(line.slice(0, at)) + line.slice(at);
      })
      .join('\n');
  }

  const { masked, comments } = maskComments(sql);
  const ported = pass === 'limit' ? limitPass(masked) : blockRewrites[pass](masked);
  if (ported === null) return null;
  return unmask(ported, comments);
}

if (!lineRewrites[pass] && !blockRewrites[pass]) {
  console.error(`usage: node tools/sql-port.mjs <${[...Object.keys(lineRewrites), ...Object.keys(blockRewrites)].join('|')}> [--dry]`);
  process.exit(1);
}

let files = 0;
let blocks = 0;
const refused = [];

for (const file of walk(API)) {
  const source = readFileSync(file, 'utf8');
  const where = relative(API, file).replaceAll('\\', '/');
  let touched = false;

  const next = source.replace(/(\$""")([\s\S]*?)(""")/g, (whole, open, sql, close) => {
    const ported = transform(sql);
    if (ported === null) {
      refused.push(`${where} (could not place a TOP)`);
      return whole;
    }
    if (ported === sql) return whole;

    // Every rewrite here must be a fixed point: applying it to its own output
    // has to change nothing. A rule that appends rather than replaces looks
    // correct on the first run and compounds on every one after — which is
    // exactly what the bare-boolean rule did, quietly, for seven statements.
    // Checked per statement rather than per run, so the report names the file.
    if (transform(ported) !== ported) {
      refused.push(`${where} (not idempotent)`);
      return whole;
    }

    // A rewrite that wraps an expression is one bracket away from changing
    // what the statement means while still parsing. Refuse rather than write.
    if (balance(ported) !== balance(sql)) {
      refused.push(`${where} (brackets)`);
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
    (refused.length ? `\n  REFUSED: ${[...new Set(refused)].join(', ')}` : '')
);
