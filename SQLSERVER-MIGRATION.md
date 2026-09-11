# Moving to SQL Server

Where the move stands, what is left, and how the numbers here were arrived at.

Every figure below came from running something, not from reading. `sqlcmd`
against a real engine is the authority, and the two tools that produce these
numbers are in `tools/`.

## Done

**The schema.** 37 tables, one view and four check constraints, generated from
the EF model and applied to SQL Server. `dotnet ef database update` builds it
from nothing.

**The model, which was not a description of the database.** It had been
scaffolded from PostgreSQL once and never regenerated, and eight migrations had
landed since. Eleven tables the application queries every day, a view, and
twenty-five columns were absent — invisible for as long as the schema was being
read *into* the model, and unmissable the moment one had to be generated *from*
it. All of it is now in `AutoPartsContext.LateSchema.cs`, written from the
migrations that added it. **Every table and column the raw SQL names now
resolves.**

**The provider.** `DATABASE_PROVIDER` chooses; the shape of the connection
string answers when it is unset. Everything currently deployed reads as
PostgreSQL and is untouched.

**A harness.** `AutoPartsHub.Tests/SqlServer` runs against a real engine and
skips, visibly, when there is not one — so the suite still finishes in seconds
on a machine with nothing installed.

Five engine differences were found by the engine refusing them, and are fixed:
a self-referencing foreign key SQL Server will not cascade, three filtered
index predicates written as `= true`, forty-three `nvarchar(max)` columns
that could not be indexed, and two check constraints that compare booleans.

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

### 1. ~~The model is not a description of the database~~ — done

This was the blocker, and it was not visible before a schema had to be
generated. It is fixed; the detail stays because it is why the rest of the plan
can be trusted, and because the same thing will happen again the next time the
storefront migrates something.

The EF model was scaffolded from PostgreSQL once and never updated. Eight
Prisma migrations landed after it. The application reads the tables they added
through raw SQL — which works against PostgreSQL, because the columns really
are there — so nothing ever failed to say the model had fallen behind.

**The eleven tables it queries that the model did not know about:**

| Table | Added by |
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

**And twenty-five columns** on tables that were already there, across `Product`,
`Order`, `Supplier`, `MarkupRule`, `PriceList`, `PriceListItem`, `VehicleModel`
and `VehicleVariant`.

The dialect check only ever named seven of those columns, because a batch with
a syntax error never reaches name resolution — so most statements were refused
before SQL Server looked at whether their tables existed. The other eighteen
came from reading every `ADD COLUMN` in the storefront's migrations and
checking each against the entities. The last one, `GoodsCategory.markupMinAmount`,
was found by neither: it belongs to a table that did not exist yet when the
audit ran, and only appeared once the table did.

Two more dialect differences turned up in the constraints while writing this.
PostgreSQL states a pairing as an equality between two comparisons —
`("markupType" IS NULL) = ("markupValue" IS NULL)` — which reads naturally
because a comparison there is a value of type boolean. SQL Server has no
boolean type at all: a comparison is a condition, not a value, and there is
nothing to put on either side of that `=`. Both became the disjunction they
mean.

### 2. ~~The dialect~~ — one statement left

`node tools/sql-dialect-check.mjs` extracts all 173 raw statements, replaces
each `{interpolation}` with NULL, and writes a batch that asks SQL
Server to parse and bind every one of them under `SET NOEXEC ON` — so names
are checked and nothing runs. Today:

> **1 of 172.** The remaining statement is the search's fuzzy fallback, which
> uses pg_trgm's `word_similarity`. There is no SQL Server equivalent and there
> was never going to be one — the backlog already scopes its replacement as
> T-067, a full-text catalogue on names and a prefix seek on numbers. That is a
> feature to build rather than a line to rewrite.
>
> The count went 108, 86, 61, 45, 33, 20, 13, 7, 3, 1.

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

### 3. ~~What the check cannot tell you~~ — done

`NOEXEC` checks shape and names. It does not check meaning: a `LIMIT` rewritten
to a `TOP` that lost its `ORDER BY` parses perfectly and returns different
rows.

`AutoPartsHub.Tests/SqlServer` now seeds a small catalogue whose numbers are
chosen to make a wrong answer visible rather than plausible — the preferred
supplier is the dearer of the two live ones, and the stopped supplier's offer
is both the cheapest and the highest priority. Twelve tests cover the rewrites
where being wrong would be invisible.

One of them found a real bug. `CURRENT_TIMESTAMP` is the server's LOCAL time in
SQL Server and `SYSUTCDATETIME` is UTC; translating `now()` and leaving the
schema defaults alone put both into the same columns, three hours apart, so a
row came back with a `lastSeenAt` earlier than its `firstSeenAt`. Thirty-one
sites now say UTC and a test reads the defaults the migration created.

### 4. ~~What has not been touched~~ — done

The dialect check reads statements; it does not run the application, so two
things it could not see were left PostgreSQL-shaped.

**Exception handling.** Three places caught `PostgresException` and did
something other than let a failed write become a 500: two retry a reference
collision, one turns a CHECK violation on the shelves into a sentence telling
an admin to run the reconciliation. Against SQL Server none of them fired —
the exception is a `SqlException` carrying a number rather than a SQLSTATE.
That is the worst shape of bug available here, because those paths only run
when something has already gone wrong and are the least likely to be tried by
hand before a cutover.

`DatabaseRefusals` classifies both engines' exceptions into what the
application actually cares about, and the SQL Server numbers are asserted by
provoking each violation against a real engine rather than cited from a table.
547 in particular covers both CHECK and FOREIGN KEY and only the message says
which — reading one as the other would tell an admin to hunt a stock
discrepancy that does not exist.

**`ConnectionString`.** The note here previously said this file was "wrong the
moment PostgreSQL is not the deployed engine". Checking it showed that was
overstated: `Normalise` converts a `postgres://` URL and passes everything
else through untouched, so a SQL Server string already survived it intact.

The real gap was a different one, and had no test. Nothing noticed when
`DATABASE_PROVIDER` and the connection string disagreed — half a deployment
moved and half not — and the provider's own complaint about an unrecognised
keyword reads as a typo in the string rather than as the wrong engine. That
now refuses at startup and says which half is left over.

### 5. The cutover

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
