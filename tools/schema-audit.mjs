// What PostgreSQL's schema has and SQL Server's does not.
//
//   node tools/schema-audit.mjs            # every difference, grouped
//   node tools/schema-audit.mjs --indexes  # include non-unique indexes
//
// The EF model was scaffolded from PostgreSQL once. Scaffolding reads a schema
// INTO a model, and what it does not read simply is not there afterwards —
// which cost nothing for as long as the schema was the one being read, and
// costs a bug per omission now that a schema is GENERATED from the model.
//
// The defaults were the first class found, and they were found by a customer
// path: registration answered 500 because Client.discountPercent is NOT NULL
// and had no default. Fourteen columns were in that state. This tool exists
// because "fourteen of one kind" is a reason to go looking for the others
// rather than to feel finished.
//
// It compares Prisma's schema — which IS the PostgreSQL one, see README on
// which repository owns what — against SQL Server's catalogue. Both are read;
// nothing is assumed.
//
// One caveat worth knowing before trusting a finding: Prisma's schema is the
// SOURCE of the PostgreSQL schema, not a transcript of it, and the two do not
// agree everywhere. A list field is the known case — always present in
// Prisma's client types, and a plain nullable array in the column. When a
// finding here is surprising, the ground truth is the DDL in
// `prisma/migrations/*/migration.sql`, and it has settled one already.
//
// The comparison is by SHAPE, not by name. An index called
// Product_partNumber_key here and IX_Product_partNumber there is the same
// index, and a report that said otherwise would be noise nobody reads twice.

import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';

const PRISMA = process.env.PRISMA_SCHEMA
  ?? 'C:/Users/Aio/autoparts-hub/autoparts-hub/prisma/schema.prisma';

const SQLCMD = process.env.SQLCMD
  ?? 'C:/Program Files/Microsoft SQL Server/Client SDK/ODBC/170/Tools/Binn/sqlcmd.exe';

const SERVER = process.env.AUDIT_SERVER ?? '(localdb)\\MSSQLLocalDB';
const DATABASE = process.env.AUDIT_DATABASE ?? 'AutoPartsHub';

const withIndexes = process.argv.includes('--indexes');

// ------------------------------------------------------------------ Prisma

/** Prisma's scalar types. Anything else names another model, so it is not a column. */
const SCALARS = new Set([
  'String', 'Boolean', 'Int', 'BigInt', 'Float', 'Decimal', 'DateTime', 'Json', 'Bytes',
]);

/** Defaults Prisma produces in the client, so PostgreSQL has none either. */
const APPLICATION_SIDE = /^(cuid|uuid|auto|autoincrement|dbgenerated)\(/;

function parsePrisma(source) {
  const models = new Map();
  let model = null;

  for (const raw of source.split(/\r?\n/)) {
    const opening = raw.match(/^\s*model\s+(\w+)\s*\{/);
    if (opening) {
      model = {
        name: opening[1],
        columns: new Map(),
        uniques: [],   // arrays of column names
        indexes: [],   // arrays of column names
        keys: [],      // the primary key's columns
        foreignKeys: [],
      };
      models.set(model.name, model);
      continue;
    }
    if (model && /^\s*\}/.test(raw)) { model = null; continue; }
    if (!model) continue;

    // A comment can contain anything, including things that look like
    // attributes. Only the code half of a line is schema.
    const line = raw.split('///')[0].split('//')[0];

    // Block attributes first: they start with @@ and are not fields.
    const block = line.match(/^\s*@@(unique|index|id)\s*\(\s*\[([^\]]*)\]/);
    if (block) {
      const [, kind, inside] = block;
      // `createdAt(sort: Desc)` — the sort is not part of the shape.
      const columns = inside.split(',').map((c) => c.trim().replace(/\(.*$/, '')).filter(Boolean);
      if (kind === 'unique') model.uniques.push(columns);
      if (kind === 'index') model.indexes.push(columns);
      if (kind === 'id') model.keys = columns;
      continue;
    }

    const field = line.match(/^\s*(\w+)\s+(\w+)(\?|\[\])?\s*(.*)$/);
    if (!field) continue;
    const [, name, type, modifier, attributes] = field;

    // The object side of a relation is not a column; the scalar holding the
    // key is, and it is declared separately.
    if (!SCALARS.has(type)) {
      const relation = attributes.match(/@relation\(([^)]*)\)/);
      const fields = relation?.[1].match(/fields:\s*\[([^\]]*)\]/);
      const references = relation?.[1].match(/references:\s*\[([^\]]*)\]/);
      if (fields && references) {
        model.foreignKeys.push({
          columns: fields[1].split(',').map((c) => c.trim()),
          table: type,
          onDelete: relation[1].match(/onDelete:\s*(\w+)/)?.[1]
            // Prisma's own default, which is what PostgreSQL was given.
            ?? (modifier === '?' ? 'SetNull' : 'Restrict'),
        });
      }
      continue;
    }

    const fallback = attributes.match(/@default\(([^)]*(?:\([^)]*\))?[^)]*)\)/);
    const value = fallback?.[1].trim();

    model.columns.set(name, {
      name,
      type,
      // A list is not NOT NULL.
      //
      // Prisma's client types say a String[] field is always present, and it
      // is tempting to read that as a constraint. It is not one: the column
      // Prisma generates is `TEXT[]` with no NOT NULL, so PostgreSQL accepts
      // a null there and so should SQL Server. Reported once as a difference
      // before the migration SQL was checked — which is the reason this
      // comment exists rather than the fix alone.
      optional: modifier === '?' || modifier === '[]',
      hasDefault: value !== undefined && !APPLICATION_SIDE.test(value),
      default: value,
    });

    if (/@id\b/.test(attributes)) model.keys = [name];
    if (/@unique\b/.test(attributes)) model.uniques.push([name]);
  }

  return models;
}

// -------------------------------------------------------------- SQL Server

function ask(query) {
  const out = execFileSync(
    SQLCMD,
    ['-S', SERVER, '-d', DATABASE, '-I', '-b', '-h', '-1', '-W', '-Q', `SET NOCOUNT ON;${query}`],
    { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });

  return out.split(/\r?\n/).map((l) => l.trim()).filter(Boolean).map((l) => l.split('|'));
}

function readSqlServer() {
  const tables = new Map();

  for (const [name] of ask(`SELECT name FROM sys.tables;`)) {
    tables.set(name, {
      name, columns: new Map(), uniques: [], indexes: [], keys: [], foreignKeys: [],
    });
  }

  const columns = ask(`
    SELECT OBJECT_NAME(c.object_id) + '|' + c.name + '|' + t.name + '|'
         + CAST(c.is_nullable AS varchar(1)) + '|'
         + CASE WHEN d.object_id IS NULL THEN '0' ELSE '1' END
    FROM sys.columns c
    JOIN sys.types t ON t.user_type_id = c.user_type_id
    JOIN sys.tables tb ON tb.object_id = c.object_id
    LEFT JOIN sys.default_constraints d
      ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
    WHERE c.is_computed = 0;`);

  for (const [table, column, type, nullable, hasDefault] of columns) {
    tables.get(table)?.columns.set(column, {
      name: column, type, optional: nullable === '1', hasDefault: hasDefault === '1',
    });
  }

  const indexes = ask(`
    SELECT OBJECT_NAME(i.object_id) + '|' + i.name + '|'
         + CAST(i.is_unique AS varchar(1)) + '|'
         + CAST(i.is_primary_key AS varchar(1)) + '|'
         + STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
    FROM sys.indexes i
    JOIN sys.index_columns ic
      ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
    JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id
    JOIN sys.tables tb ON tb.object_id = i.object_id
    WHERE i.name IS NOT NULL
    GROUP BY i.object_id, i.name, i.is_unique, i.is_primary_key;`);

  for (const [table, , unique, primary, columnList] of indexes) {
    const held = tables.get(table);
    if (!held) continue;
    const columnNames = columnList.split(',');
    if (primary === '1') held.keys = columnNames;
    else if (unique === '1') held.uniques.push(columnNames);
    else held.indexes.push(columnNames);
  }

  const keys = ask(`
    SELECT OBJECT_NAME(f.parent_object_id) + '|' + OBJECT_NAME(f.referenced_object_id) + '|'
         -- COLLATE because this column is not in the server's collation and
         -- the concatenation will not resolve the two on its own.
         + f.delete_referential_action_desc COLLATE DATABASE_DEFAULT + '|'
         + STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY fc.constraint_column_id)
    FROM sys.foreign_keys f
    JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = f.object_id
    JOIN sys.columns c ON c.object_id = fc.parent_object_id AND c.column_id = fc.parent_column_id
    GROUP BY f.object_id, f.parent_object_id, f.referenced_object_id,
             f.delete_referential_action_desc;`);

  for (const [table, references, onDelete, columnList] of keys) {
    tables.get(table)?.foreignKeys.push({
      columns: columnList.split(','), table: references, onDelete,
    });
  }

  return tables;
}

// -------------------------------------------------------------- comparison

const shape = (columns) => columns.join(',');
const holds = (list, columns) => list.some((c) => shape(c) === shape(columns));

/**
 * Prisma's referential action, as SQL Server names it.
 *
 * Restrict and NoAction both refuse the delete; SQL Server calls that
 * NO_ACTION and has no separate Restrict, so the two are the same answer.
 */
const asSqlServer = (onDelete) => ({
  Cascade: 'CASCADE',
  SetNull: 'SET_NULL',
  SetDefault: 'SET_DEFAULT',
  Restrict: 'NO_ACTION',
  NoAction: 'NO_ACTION',
}[onDelete] ?? onDelete.toUpperCase());

/**
 * Differences SQL Server leaves no choice about.
 *
 * Each carries its reason, and each was ASKED rather than assumed — the engine
 * refused the alternative and the refusal is quoted. An allowance without that
 * is how a real difference gets filed under "known".
 *
 * Three more sat here briefly, on the belief that SQL Server refuses a second
 * path between two tables it already joins. It refuses a second CASCADE path,
 * and the first path in each of those was Restrict. Asked directly, the engine
 * accepted SET NULL on all three, and they are SET NULL now.
 */
const ALLOWED = [
  {
    what: 'Client (salesManagerId) -> Client',
    why: 'SQL Server refuses SET NULL on a self-reference (error 1785). A sales '
       + 'manager cannot be deleted while they still look after somebody, where '
       + 'PostgreSQL would let go of them. Nothing here deletes a Client.',
  },
  // The three below are one rule seen three times: SQL Server allows exactly
  // one referential action per pair of tables and refuses the second, whatever
  // it is. Asked directly rather than assumed —
  //
  //   first  CASCADE                    accepted
  //   second CASCADE to the same table  refused
  //   second as NO ACTION               accepted
  //
  // So the cascade goes to the relation that matters and the rest refuse the
  // delete. PostgreSQL keeps the better behaviour because it can; crippling
  // the live schema to match a limitation of the one being moved to would be
  // the wrong direction.
  {
    what: 'ManagerAccess (grantedById) -> Client',
    why: 'ManagerAccess already cascades from Client on managerId, and SQL Server '
       + 'allows one action per pair of tables. Deleting the admin who granted a '
       + 'reach is refused here and clears the column there.',
  },
  {
    what: 'ExtraClient (clientId) -> Client',
    why: 'ExtraClient already cascades from Client on managerId — the relation that '
       + 'matters, since a salesperson leaving should take their grants with them. '
       + 'Deleting a CUSTOMER who appears in somebody granted list is refused here '
       + 'and tidied away there.',
  },
  {
    what: 'ExtraClient (grantedById) -> Client',
    why: 'The third relation to Client on the same table, for the same reason.',
  },
];

const wanted = parsePrisma(readFileSync(PRISMA, 'utf8'));
const held = readSqlServer();

const findings = { tables: [], columns: [], nullability: [], defaults: [], uniques: [], keys: [], deletes: [], indexes: [] };

for (const [name, model] of wanted) {
  const there = held.get(name);
  if (!there) { findings.tables.push(name); continue; }

  for (const [column, want] of model.columns) {
    const has = there.columns.get(column);
    if (!has) { findings.columns.push(`${name}.${column}  ${want.type}`); continue; }

    if (want.optional !== has.optional) {
      findings.nullability.push(
        `${name}.${column}  postgres ${want.optional ? 'NULL' : 'NOT NULL'}`
        + `, sql server ${has.optional ? 'NULL' : 'NOT NULL'}`);
    }
    if (want.hasDefault && !has.hasDefault) {
      findings.defaults.push(`${name}.${column}  @default(${want.default})`);
    }
  }

  for (const columns of model.uniques) {
    // A primary key is a unique constraint by another name.
    if (holds(there.uniques, columns) || shape(there.keys) === shape(columns)) continue;
    findings.uniques.push(`${name} (${columns.join(', ')})`);
  }

  for (const key of model.foreignKeys) {
    const there_ = there.foreignKeys.find((f) => shape(f.columns) === shape(key.columns));
    if (!there_) {
      findings.keys.push(`${name} (${key.columns.join(', ')}) -> ${key.table}`);
      continue;
    }
    if (there_.onDelete !== asSqlServer(key.onDelete)) {
      findings.deletes.push(
        `${name} (${key.columns.join(', ')}) -> ${key.table}`
        + `  postgres ${asSqlServer(key.onDelete)}, sql server ${there_.onDelete}`);
    }
  }

  for (const columns of model.indexes) {
    // An index is satisfied by anything that leads with the same columns —
    // a unique constraint on (a, b) answers a lookup by (a, b), and the
    // primary key answers one on its own columns.
    const satisfied = holds(there.indexes, columns)
      || holds(there.uniques, columns)
      || shape(there.keys) === shape(columns)
      || [...there.indexes, ...there.uniques].some((c) => shape(c).startsWith(shape(columns) + ','));
    if (!satisfied) findings.indexes.push(`${name} (${columns.join(', ')})`);
  }
}

// ------------------------------------------------------------------ report

const report = (title, lines, why) => {
  if (lines.length === 0) return 0;
  console.log(`\n${title} — ${lines.length}`);
  console.log(`  ${why}`);
  for (const line of lines) console.log(`    ${line}`);
  return lines.length;
};

console.log(`${wanted.size} models in the Prisma schema, ${held.size} tables on SQL Server.`);

let serious = 0;
serious += report('TABLES MISSING', findings.tables,
  'every query against them fails.');
serious += report('COLUMNS MISSING', findings.columns,
  'every query naming them fails.');
serious += report('NULLABILITY DIFFERS', findings.nullability,
  'NOT NULL where postgres allows NULL refuses writes the other API makes.');
serious += report('DEFAULTS MISSING', findings.defaults,
  'a NOT NULL column with no default refuses every INSERT that omits it.');
serious += report('UNIQUE CONSTRAINTS MISSING', findings.uniques,
  'duplicates get in, and nothing says so until something reads them.');
serious += report('FOREIGN KEYS MISSING', findings.keys,
  'orphans get in.');

const allowed = findings.deletes.filter((d) => ALLOWED.some((a) => d.startsWith(a.what)));
const unexplained = findings.deletes.filter((d) => !allowed.includes(d));

serious += report('DELETE BEHAVIOUR DIFFERS', unexplained,
  'a SET NULL that became NO_ACTION turns a working delete into an error.');

if (allowed.length > 0) {
  console.log(`\nAllowed, because the engine refuses the alternative — ${allowed.length}`);
  for (const line of allowed) {
    console.log(`    ${line}`);
    console.log(`      ${ALLOWED.find((a) => line.startsWith(a.what)).why}`);
  }
}

if (withIndexes) {
  report('INDEXES MISSING', findings.indexes,
    'not wrong, only slow — and only where the table grows.');
} else if (findings.indexes.length > 0) {
  console.log(`\n${findings.indexes.length} non-unique indexes missing. --indexes to list them.`);
}

console.log(serious === 0
  ? '\nNothing unexplained that changes what a write does.'
  : `\n${serious} differences that change what a write does.`);
