# Moving to SQL Server

Where the move stands, what is left, and how the numbers here were arrived at.

Every figure below came from running something, not from reading. `sqlcmd`
against a real engine is the authority, and the two tools that produce these
numbers are in `tools/`.

## Done

**The schema.** 27 tables, 81 indexes, 30 foreign keys, generated from the EF
model and applied to SQL Server. `dotnet ef database update` builds it from
nothing.

**The provider.** `DATABASE_PROVIDER` chooses; the shape of the connection
string answers when it is unset. Everything currently deployed reads as
PostgreSQL and is untouched.

**A harness.** `AutoPartsHub.Tests/SqlServer` runs against a real engine and
skips, visibly, when there is not one — so the suite still finishes in seconds
on a machine with nothing installed.

Three engine differences were found by the engine refusing them, and are fixed:
a self-referencing foreign key SQL Server will not cascade, three filtered
index predicates written as `= true`, and forty-three `nvarchar(max)` columns
that could not be indexed.

## The one assumption that held

SQL Server reads PostgreSQL's quoted identifiers identically.
`SELECT p."partNumber" FROM "Product" p` is the same statement to both engines
as long as `QUOTED_IDENTIFIER` is on, and SqlClient turns it on for every
connection it opens. `SchemaTests.PostgresStyleQuotingReadsTheSameHere` asserts
both halves.

This is why the port is a few hundred edits and not a rewrite: **the
identifiers in all 1,978 lines of raw SQL stay exactly as they are.** Only the
dialect around them moves.

## Left to do, in order

### 1. The model is not a description of the database

This is the blocker, and it was not visible before the schema was generated.

The EF model was scaffolded from PostgreSQL once and never updated. Eight
Prisma migrations have landed since. The application reads the tables they
added through raw SQL — which works against PostgreSQL, because the columns are
really there — so nothing ever failed to say the model had fallen behind.

**Eleven tables the application queries do not exist in the generated schema:**

| Missing | Added by |
|---|---|
| `ProductSpec` | `20260822160000_add_product_specs` |
| `ProductBarcode` | `20260823090000_add_barcodes_and_packaging` |
| `GoodsCategory` | `20260823120000_add_goods_categories` |
| `VinLookup` | `20260823180000_vin_lookup_log` |
| `PriceListImport`, `PriceListImportRow` | `20260827090000_price_list_imports` |
| `SearchMiss` | `20260827120000_search_misses` |
| `Ticket`, `TicketMessage` | `20260827130000_support_tickets` |
| `SupplierOffer` | `20260828090000_supplier_offers` |
| `BestOffer` — a **view**, not a table | `20260828090000_supplier_offers` |

**Seven columns, on tables that do exist:** `goodsCategoryId`, `markupPercent`,
`minOrderAmount`, `partType`, `priority`, `weightComplete`, `weightGrams`.

That list is a floor, not a total. A batch with a syntax error never reaches
name resolution, so 134 of the statements were refused before SQL Server ever
checked whether their tables existed. The real figure is knowable only after
step 2, by re-running the check.

The model has to be completed first: everything below is verified against the
schema the model produces, and a schema missing a third of its tables verifies
nothing.

### 2. The dialect

`node tools/sql-dialect-check.mjs` extracts all 173 raw statements, replaces
each `{interpolation}` with a parameter, and writes a batch that asks SQL
Server to parse and bind every one of them under `SET NOEXEC ON` — so names
are checked and nothing runs. Today:

> **134 of 172 statements SQL Server refuses.**

What is in them, counted in the SQL itself:

| Construct | Occurrences | Becomes |
|---|---:|---|
| `::type` cast | 203 | `CAST(x AS t)`, or nothing — most are redundant |
| `LIMIT` / `OFFSET` | 36 | `OFFSET … FETCH NEXT`, which needs an `ORDER BY` |
| `LATERAL` | 29 | `CROSS APPLY` / `OUTER APPLY` |
| `TRUE` / `FALSE` | 33 | `1` / `0` |
| `= ANY(@array)` | 20 | `IN (SELECT value FROM OPENJSON(@json))` |
| `ILIKE` | 14 | `LIKE` — the default collation is already case-insensitive |
| `now()` | 12 | `SYSUTCDATETIME()` |
| `similarity()`, `unnest()` | 12 | full-text and `OPENJSON` — see below |
| `ON CONFLICT` | 5 | `MERGE`, or `UPDATE` then `INSERT WHERE NOT EXISTS` |
| `RETURNING` | 4 | `OUTPUT` |
| `to_char` | 4 | `FORMAT` / `CONVERT` |

Most of it is mechanical. Three parts are not:

- **`= ANY(@array)`.** Npgsql passes a C# array as a PostgreSQL array and SQL
  Server has no equivalent parameter. `OPENJSON` over a JSON-serialised array
  is the safe answer — `STRING_SPLIT` would break on any value containing the
  delimiter, and part numbers are caller input.
- **`pg_trgm` similarity.** The search's fuzzy fallback, used when nothing
  matched as typed. There is no equivalent; the backlog already specifies what
  replaces it (T-067: a full-text catalogue on names, a prefix seek on
  numbers), so this is that task arriving early rather than a translation.
- **`LIMIT` without `ORDER BY`.** PostgreSQL allows it; `OFFSET … FETCH`
  requires an order. Each site needs a decision about what the order should be,
  and "whatever the database returned" is not one — that is how a paged list
  starts repeating rows.

The largest concentrations are `AdminReferenceEndpoints.cs` (13),
`AdminSiteWriteEndpoints.cs` (10), `AdminPriceListWriteEndpoints.cs` (9),
`AdminDeskEndpoints.cs` (9) and `Admin/MarkupRules.cs` (9).

### 3. What the check cannot tell you

`NOEXEC` checks shape and names. It does not check meaning: a `LIMIT` rewritten
to a `TOP` that lost its `ORDER BY` parses perfectly and returns different
rows. Seeded data and assertions on results are the only thing that catches
that, which is T-003 and T-025.

### 4. The cutover

The two applications share one database today. Whatever else is decided, that
stops being true the moment this one is pointed at SQL Server — so the
storefront's PostgreSQL and this schema diverge from that moment, and the data
has to be moved rather than mirrored.

## Reproducing the numbers

```bash
SqlLocalDB start MSSQLLocalDB
dotnet ef database update --project AutoPartsHub.Api
node tools/sql-dialect-check.mjs
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AutoPartsHub -i tools/.sql-check.sql
```

Statements that fail are the ones preceded by a `### n file:line` marker in the
output.
