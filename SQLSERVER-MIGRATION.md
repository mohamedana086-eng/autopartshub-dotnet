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
string answers when it is unset, and a setting that disagrees with its string
is refused rather than left to the driver.

**But there is only one engine now, and this should be said plainly.** The
raw SQL was ported in place, not duplicated per provider: `OPENJSON` appears
32 times, `SYSUTCDATETIME` 116, `OUTER APPLY` 27, across 32 files. Pointed
at PostgreSQL this application selects Npgsql and then fails on nearly every
query it runs. The provider switch is a guard against a half-configured
deployment, not a way back — and the way back, if one is ever wanted, is the
git history rather than a setting.

**A harness.** `AutoPartsHub.Tests/SqlServer` runs against a real engine and
skips, visibly, when there is not one — so the suite still finishes in seconds
on a machine with nothing installed.

**Fourteen column defaults the scaffold did not bring across.** Every one on a
NOT NULL column, so every one a 500 on any INSERT that did not name it —
registration was the first found, by running it. `tools/schema-audit.mjs`
compares Prisma's schema against SQL Server's catalogue and found the rest;
it reports nothing missing. Section 2b.

**And the rest of that class, found by looking.** Fourteen of one kind is a
reason to go looking rather than to feel finished.
`tools/schema-audit.mjs` compares every table, column, nullability, default,
unique constraint, foreign key, index and delete action. It found one missing
unique, three missing foreign keys and three missing indexes. All fixed; it
reports nothing unexplained. Section 2c.

**Account recovery (T-196).** The four routes the storefront called and nothing
answered — forgot, reset, confirm, resend — and the confirmation link
registration had stopped sending. Section 6.

**The near-miss search (T-067).** The last PostgreSQL-only feature, and the
only one that was a feature rather than a translation. pg_trgm's
`word_similarity` scanned the whole catalogue on every search that came up
empty; it is two index seeks now, with the acceptance criterion asserted from
the execution plan. Section 5.

Four engine differences were found by the engine refusing them, and are fixed:
a self-referencing foreign key SQL Server will not cascade, three filtered
index predicates written as `= true`, forty-three `nvarchar(max)` columns
that could not be indexed, and two check constraints that compare booleans.
(This said "five" and then listed four. The fifth it was reaching for is the
one nothing refused — `CURRENT_TIMESTAMP` being local time — which is in
section 3, where it belongs: it was found by an assertion, not by a refusal.)

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

### 2. ~~The dialect~~ — one refusal, and it is the instance's

`node tools/sql-dialect-check.mjs` extracts all 202 raw statements, replaces
each `{interpolation}` with NULL, and writes a batch that asks SQL
Server to parse and bind every one of them under `SET NOEXEC ON` — so names
are checked and nothing runs. Today:

> **1 of 202**, and that one is not about the statement:
>
> ```
> Cannot use a CONTAINS or FREETEXT predicate on table 'Product'
> because it is not full-text indexed.
> ```
>
> It is the near-miss search's name lane (T-067). The statement parses and
> binds; what is missing is Full-Text Search, which LocalDB cannot host on any
> edition. On an instance that has the component the migration creates the
> catalogue and this refusal goes away — and on one that does not, the
> application asks the same question at startup and takes the other lane
> rather than failing. See `FullTextSearch`.
>
> The count went 108, 86, 61, 45, 33, 20, 13, 7, 3, 2.

**A correction.** This section previously said "1 of 172", and that was wrong
in a way worth recording rather than editing away. Two statements were being
refused: the fuzzy fallback everyone knew about, and `VehicleFinder`'s options
query, whose year filter was the one of nine left as the bare boolean
PostgreSQL allows in a select list. The second was sitting in the same output
under its own marker and was read as the first.

That statement would have failed on every call — nothing in it half worked,
because the column it produces is compared to `1` eight times further down the
same query. It had also acquired a second fault that no parse check could have
found: the year branch of its UNION returns a number where every other branch
returns a name, so the whole result tried to convert "Renault" to bigint. The
cast that prevented it in the original was removed as one of the redundant
ones, and it was not redundant.

Both are fixed, and `VehicleFinderTests` now runs the finder against real rows
instead of parsing it.

### 2a. What a parse check cannot see

Worth stating plainly, because six bugs escaped through it:

- **It does not evaluate constants.** `regexp_replace(x, p, '', 'g')` binds
  perfectly — SQL Server's fourth argument is `start`, an int, and the `'g'`
  is converted only when the statement runs, where it fails every time. Six
  statements carried it. They are gone now: the normalised form is a stored
  column, so no query computes one.
- **It does not resolve union types.** Every branch parses; the conversion
  between them happens on execution.
- **It cannot see a column the statement does not mention.** An INSERT naming
  the columns it cares about is valid whatever the others default to — and if
  one of them is NOT NULL with no default, it fails every time it runs. See
  section 2b.
- **It does not read a single row, so it never checks a TYPE against the C#
  waiting for it.** PostgreSQL has a boolean type and SQL Server does not, so
  every `(a = b)` that was a VALUE became
  `CASE WHEN a = b THEN 1 ELSE 0 END` — correct SQL, wrong type. It yields an
  int, SqlClient hands that to `GetBoolean`, and the read throws. Three
  statements shipped that way: the whole supplier-offer editor
  (`PUT /api/admin/products/{id}/offers`) and both client-detail reads, each
  answering 500. One word fixes each — `CAST(… AS bit)` — and the trouble is
  entirely in noticing.

  Found by a harness stumbling into one of them. `BooleanColumnTests` now
  pairs every `SqlQuery<T>` with the property each alias lands on and fails
  when a bare CASE maps to a `bool`; it leaves `VehicleFinder`'s nine filter
  flags alone, because those are compared to `1` inside their own statement
  and no C# property carries them.

The first two are caught by running the statement against rows. The third is
not caught by that either, if the rows come from a fixture that names
everything — which is what fixtures do.

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
| `similarity()`, `unnest()` | 12 | prefix seeks, full text, and `OPENJSON` — see below |
| `ON CONFLICT` | 5 | `MERGE`, or `UPDATE` then `INSERT WHERE NOT EXISTS` |
| `RETURNING` | 4 | `OUTPUT` |
| `to_char` | 4 | `FORMAT` / `CONVERT` |

Most of it is mechanical. Three parts are not:

- **`= ANY(@array)`.** Npgsql passes a C# array as a PostgreSQL array and SQL
  Server has no equivalent parameter. `OPENJSON` over a JSON-serialised array
  is the safe answer — `STRING_SPLIT` would break on any value containing the
  delimiter, and part numbers are caller input.
- **`pg_trgm` similarity.** The search's fallback, used when nothing matched
  as typed. There is no equivalent, and the backlog already specified what
  replaces it (T-067: a full-text catalogue on names, a prefix seek on
  numbers), so it was that task arriving early rather than a translation. Done
  — see section 5.
- **`LIMIT` without `ORDER BY`.** PostgreSQL allows it; `OFFSET … FETCH`
  requires an order. Each site needs a decision about what the order should be,
  and "whatever the database returned" is not one — that is how a paged list
  starts repeating rows.

The largest concentrations are `AdminReferenceEndpoints.cs` (13),
`AdminSiteWriteEndpoints.cs` (10), `AdminPriceListWriteEndpoints.cs` (9),
`AdminDeskEndpoints.cs` (9) and `Admin/MarkupRules.cs` (9).

### 2b. The defaults the scaffold did not bring across

Found by registering an account. `POST /api/auth/register` answered **500**:

```
Cannot insert the value NULL into column 'discountPercent', table 'Client';
column does not allow nulls.
```

The same fault as section 1, one layer down. Scaffolding recorded each column's
type and nullability but not, for fourteen of them, the default behind it. That
was invisible while the schema was being read INTO the model — the defaults
were really there, and every INSERT relied on them. It stopped being invisible
the moment a schema was generated FROM the model.

All fourteen are NOT NULL, so there is no gentle version: each is a 500 on any
INSERT that does not name that column.

| Table | Column | | Table | Column |
|---|---|---|---|---|
| Client | discountPercent | | StockLevel | quantity |
| ClientCategory | minOrderAmount | | StockLevel | reserved |
| ClientCategory | requiresApproval | | Warehouse | priority |
| Currency | isBase | | MarkupRule | priority |
| Interchange | exactMatch | | PriceList | active |
| Interchange | isOEM | | ProductImage | sortOrder |
| Manufacturer | isOEM | | VehicleSystem | order |

`tools/schema-audit.mjs` found the other thirteen by comparing Prisma's
schema — which IS the PostgreSQL one — against SQL Server's catalogue, so they
were found by reading rather than one at a time by whoever hit them. It reports
nothing missing now. **Run it after any schema change.**

There is a smaller lesson in `Interchange.exactMatch`. It had already been met
once, while writing the near-miss fixtures: an INSERT failed, the column was
added to it, and the work carried on. That is the shape this class of bug hides
in — in a fixture it looks like a fixture detail, and only in an endpoint does
it look like what it is.

### 2c. The rest of the same class

Fourteen missing defaults is not a coincidence, it is a sample.
`tools/schema-audit.mjs` reads Prisma's schema and SQL Server's catalogue and
compares **every** table, column, nullability, default, unique constraint,
foreign key, index and delete action — by shape rather than by name, because an
index called `Product_partNumber_key` in one and `IX_Product_partNumber` in
the other is the same index and a report saying otherwise is noise.

What it found beyond the defaults:

| | What it was costing |
|---|---|
| `GoodsCategory.name` had no unique | two categories of the same name: an admin list with one row in it twice, and a markup that depends on which of them a part was filed under |
| `Order.statusChangedById`, `TicketMessage.authorId`, `PriceListImport.uploadedById` had no foreign key | a row could name an account that never existed |
| `Order (status, createdAt)`, `VinLookup (decodedAt)`, `SearchMiss (searches)` had no index | the desk list and two admin reports, each scanning |

All are fixed. It reports nothing unexplained now.

**Two things it got wrong on the way, both worth keeping.**

*A list is not NOT NULL.* It reported `VehicleMake.wmiCodes` as NOT NULL in
PostgreSQL and nullable here, because Prisma's client types say a `String[]`
field is always present. The column Prisma generates is `"wmiCodes" TEXT[]`
with no NOT NULL — checked in the migration SQL rather than argued about. So
Prisma's schema is the SOURCE of the PostgreSQL schema and not a transcript of
it, and where a finding is surprising the DDL in `prisma/migrations` is what
settles it.

*Three foreign keys were made NO ACTION on a guess.* The belief was that SQL
Server refuses a second path between two tables it already joins. It refuses a
second CASCADE path — and the first path in each of these is Restrict, so
there was no conflict. Asked directly, the engine accepted SET NULL on all
three:

```
Order.statusChangedById      SET NULL: accepted
TicketMessage.authorId       SET NULL: accepted
PriceListImport.uploadedById SET NULL: accepted
Client.salesManagerId        SET NULL: Could not create constraint or index.
```

Three now match PostgreSQL. The fourth is a self-reference, which SQL Server
will not do at all, and it is the one difference the audit still reports —
with its reason printed beside it, because an allowance without one is how a
real difference gets filed under "known".

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


### 5. T-067 — the near-miss search

The last PostgreSQL-only thing in the application, and the only one that was a
feature rather than a translation.

**What it was.** When a search matched nothing, `word_similarity` scored the
query against every product name, every manufacturer name and every part
number, kept anything over 0.45 and sorted it. A full scan on every search
that came up empty — which is exactly the search a customer repeats, having
typed it wrong once.

**What it is.** Two lanes, each an index seek, each capped at 25:

| lane | reached by | index |
|---|---|---|
| part numbers | `partNumberNormalised LIKE 'ABC12%'` | `Product_partNumberNormalised_idx`, `Interchange_targetPartNoNormalised_idx` |
| names, with full text | `CONTAINSTABLE` with prefix terms | the `AutoPartsSearch` catalogue |
| names, without | `name LIKE 'brake pa%'` | `Product_name_idx` |
| manufacturers | `name LIKE 'MANN%'` | `Manufacturer_name_key` |

Numbers are asked first: three normalised characters of a part number agreeing
is a deliberate act, three characters of a name agreeing is a coincidence the
catalogue is full of.

**The normalised columns.** Part numbers are stored as they are printed —
`0 986 424 815`, `W712/30` — and every lookup used to strip the separators
per row with `regexp_replace`, which cannot use an index. They are stored and
indexed now (`PERSISTED` computed columns), so the database owns the rule
rather than whichever of the three writers remembered it, and the bulk lookup
seeks too. Two comments in the code had asked for exactly this.

**What was lost.** Trigram similarity matched a typo in the *middle* of a word:
"brkae pad" scored against "Brake pad set, front". Neither lane does — a prefix
seek and a full-text prefix term both need the start to be right. That is the
same property as the scan going away, seen from the other side. Truncation
("brake pa", "0 986 42") and one wrong word among right ones still work, which
is most of what a search box receives.

**What is not verified.** The `CONTAINSTABLE` statement has never been
executed. LocalDB cannot host Full-Text Search, so there is no way to run it on
this machine — it parses, it binds, and it is refused for the missing
component. Everything around it is verified: the lane that runs without full
text, the capability check that chooses between them, and the migration's
refusal to create a catalogue on an instance that cannot have one. **When the
hosting decision lands (BLK-003) on an instance with the component installed,
that statement is the first thing to exercise** —
`FullTextIsNotAvailableHereAndTheSearchKnowsIt` is written to fail there,
which is how the machine announces itself.

**The acceptance criterion** — "no leading wildcard, no wide scan" — is
asserted from the execution plan, not from the SQL. `LIKE 'ABC%'` on a column
with no index reads identically to the version that seeks; only the optimizer
can tell them apart. `PlanCapture` intercepts each statement as the
application sends it and asks SQL Server to compile it against three thousand
products, three thousand cross-references and five hundred brands. One test
points the same detector at the query that still scans on purpose, so "no
scans" cannot pass by seeing nothing.

### 6. T-196 — account recovery

The four routes CONTRACTS.md listed under "what the storefront asks for and
nothing answers", and the only place the storefront could reach a 404 by using
the app normally: `password/forgot`, `password/reset`, `email/confirm`,
`email/resend`. Registration had also stopped sending its confirmation link,
which left the banner asking somebody to confirm an address with nothing to
confirm until they pressed "send it again".

They are a port, not a design — the other API has all four, and the reasoning
in them is worth keeping word for word. `Auth/VerificationTokens.cs` is the
shared mechanism; `Endpoints/AccountRecoveryEndpoints.cs` is the four routes.

**Two things in that flow are a contract rather than a preference**, because
both APIs read one `VerificationToken` table and only the hash of a token is
stored:

- the encoding and the hash — base64url unpadded, SHA-256, lowercase hex. A
  link mailed by one API is redeemed by whichever one the customer's click
  reaches. .NET's `Convert.ToHexString` is uppercase, and using it would have
  produced "that reset link is not valid" on links that were valid, silently;
- the word "link" in every refusal about a token, which
  `pages/reset-password.page.ts` matches with `/link/i` to decide whether to
  offer a fresh one. Reword it and the page still shows the error, still looks
  right, and stops offering the one button that gets the person out of it.

Both are asserted — the first against values produced by the other API's own
crypto rather than by reading this code back to itself.

**Ported to SQL Server, not copied.** `FOR UPDATE OF v` became
`WITH (UPDLOCK, ROWLOCK)`, `CURRENT_TIMESTAMP - INTERVAL '1 hour'` became
`DATEADD(hour, -1, SYSUTCDATETIME())`, and every `CURRENT_TIMESTAMP` became
`SYSUTCDATETIME()` for the reason in section 3.

**Verified by running it**, which is how the registration 500 in section 2b was
found: register, read the token out of the outbox, confirm it, confirm it again
as a mail scanner would, ask for a reset, spend it, spend it twice, then sign
in with the old password and the new one. The suite covers the mechanism — the
locking, the expiry, the rate limit, the rollback — and the round trip covers
the part no unit can: that the routes are wired and answer.

### 7. The cutover

The two applications share one database today. Whatever else is decided, that
stops being true the moment this one is pointed at SQL Server — so the
storefront's PostgreSQL and this schema diverge from that moment, and the data
has to be moved rather than mirrored.

**`GET /health/ready` is in place for it.** A cutover has a window between a
deployment starting and its migrations finishing, and an instance in that
window answers queries against a schema two versions behind the code that is
querying it. That is the one state a load balancer must not send traffic into,
and it is the state a connection test cannot see — so readiness checks the
database AND whether its schema is current, and answers 503 until both hold.

It reports three more without deciding on them: the cache (`IPriceCache` says a
cache that is down is a slow shop, not a closed one), whether full text is
available — which says which lane the search's name half is on — and whether a
mail transport is configured, which is the difference between a password reset
that is sent and one that is accepted and never arrives.

Two things still have to be decided rather than built: where SQL Server is
hosted (BLK-003), and whether the storefront moves with it.

## Reproducing the numbers

```bash
SqlLocalDB start MSSQLLocalDB
dotnet ef database update --project AutoPartsHub.Api
node tools/sql-dialect-check.mjs
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AutoPartsHub -I -i tools/.sql-check.sql
```

Statements that fail are the ones preceded by a `### n file:line` marker in the
output. `-I` is not optional: sqlcmd turns `QUOTED_IDENTIFIER` off by default
and every statement in this application quotes its identifiers, so without it
the failures are all the same one and none of them is real.

One statement is expected to fail, on an instance without Full-Text Search —
see section 2.

```bash
node tools/schema-audit.mjs
```

Every difference between PostgreSQL's schema and this one — tables, columns,
nullability, defaults, uniques, foreign keys, delete actions, and `--indexes`
for the rest. It should print "Nothing unexplained"; anything above that line
is a write that behaves differently on the two engines. Run it after any schema
change — sections 2b and 2c.
