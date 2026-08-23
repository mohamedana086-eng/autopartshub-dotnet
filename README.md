# AutoParts Hub — .NET API

A port of the Next.js API to ASP.NET Core, reading the same PostgreSQL
database. Separate repository on purpose: the Node API is live and serving,
and nothing here touches it.

```
C:\Users\Aio\autoparts-hub\        the running site — Next API + Angular
C:\Users\Aio\autopartshub-dotnet\  this
```

## What is settled

- **PostgreSQL stays.** Same Neon database, same 25 tables, same data. No
  migration, no downtime, and the two APIs can be compared request for request
  because they read the same rows.
- **EF Core**, with raw SQL where LINQ cannot reach — see below.
- **Hosting is not decided.** Vercel does not run .NET. The code takes its
  configuration from environment variables and ships with a Dockerfile, so
  Azure, Railway, Fly or a VPS are all still open.

## Running it

```bash
cd AutoPartsHub.Api
dotnet run
```

`.env` next to the project supplies `DATABASE_URL` and `AUTH_SECRET` in
development; a deployment sets real environment variables instead and the file
is not read. It is gitignored. `DATABASE_URL` may be either a
`postgresql://user:pass@host/db` url — what every host hands you — or Npgsql's
own `Host=…;Database=…` form; see `Data/ConnectionString.cs`.

```
GET /health      the process is up
GET /health/db   it can reach the database, and how many products it can see
```

## The container

```bash
docker build -t autopartshub-api .
docker run --rm -p 8080:8080 \
  -e DATABASE_URL="postgresql://..." \
  -e AUTH_SECRET="$(openssl rand -hex 32)" \
  autopartshub-api
```

Nothing in the image names a host. Everything that differs between Azure,
Railway, Fly and a VPS arrives as an environment variable, so the same image
runs wherever the decision lands.

Two things in it are not obvious and both would fail quietly rather than
loudly, which is why each has a guard in `Program.cs`:

**ICU has to be present.** The catalogue is ordered with culture-aware
comparisons because the API this one replaces uses JavaScript's
`localeCompare`, which treats case as a secondary weight — an ordinal sort puts
"CV joint kit" before "Cabin filter". A runtime image without ICU turns on
globalization-invariant mode, which converts every `InvariantCulture`
comparison back into an ordinal one and says nothing. The app would start, the
health check would pass, and search results would come back shuffled. Hence
`aspnet:10.0-noble-chiseled-extra` rather than the bare chiseled image, and
hence a startup check that refuses to run in invariant mode.

**`PORT`, not `ASPNETCORE_HTTP_PORTS`.** Railway, Render and Cloud Run assign a
port and announce it in `PORT`; none of them read `EXPOSE`. Kestrel does not
read `PORT`. Joining the two up in the image by baking in
`ASPNETCORE_HTTP_PORTS=8080` is the obvious move and the wrong one — a default
under a name the host does not set outranks the port it actually assigned, and
the container listens politely where nobody is looking. The image sets `PORT`
instead, so a host overrides it just by doing what it already does. Precedence
is `ASPNETCORE_URLS`, then `PORT`.

### What running in Production changes

| | |
|---|---|
| `.env` | not read at all — configuration comes from the environment |
| `/dev/*` probes | not mapped; all five return 404 |
| OpenAPI | not mapped |
| the session cookie | marked `Secure` |
| a missing, placeholder or short `AUTH_SECRET` | the process exits at startup |

That last one matters more than it reads. A deployment that came up signing
cookies with the published placeholder would work perfectly and let anyone mint
an admin session, so it is a crash rather than a warning. All three cases were
checked against the published build: each exits non-zero with the sentence
naming the fix.

### What has been checked, and what has not

The image itself has **not** been built — there is no Docker on the machine
this was written on. What was checked is everything inside it: the exact
`dotnet publish -c Release` the Dockerfile runs, and that build served under
`ASPNETCORE_ENVIRONMENT=Production` with `PORT` set and no `.env` present, with
the whole comparison suite run against it — 232 reads and 49 admin writes, all
identical to the API in production today. Container layering is the part still
to prove.

### Hosting

Not decided. What the decision turns on:

- **The database is on AWS `us-east-1`.** Every request makes several round
  trips to it, so the region matters more than which company runs the host.
- **The storefront reaches the API through a Vercel rewrite**, which is a
  server-side proxy — the browser only ever sees the storefront's own origin.
  So switching over is one line in `web/vercel.json`, the session cookie keeps
  working untouched, CORS never enters into it, and rolling back is the same
  one line.
- **Do not enable scale-to-zero.** A .NET container takes a couple of seconds
  to start, and with the proxy hop in front of it the first customer after a
  quiet spell waits for all of it.

## The model

`Data/Entities` is reverse-engineered from the live database rather than
hand-written, so it describes what is actually there. Twenty-five entities,
matching the twenty-five tables the application owns.

Two things the scaffold turned up:

- The database also holds a **`neon_auth` schema** — nine tables belonging to
  Neon's own auth product, which this application does not use and does not
  model. The scaffold is restricted to `public`.
- Five **expression indexes** cannot be represented in EF's model: the trigram
  indexes on names and part numbers, and the normalised part-number indexes.
  They exist in the database and the queries that need them will use them; EF
  simply cannot describe them. They matter if this project ever takes over
  migrations, which it has not.

`_prisma_migrations` is left out of the model. It is the migration ledger, the
TypeScript runner in the other repository still owns it, and it is bookkeeping
rather than domain.

## Where LINQ will not reach

EF Core covers ordinary CRUD. These do not translate, and are written as raw
SQL through `SqlQuery` and `ExecuteSql` — the same statements the Node API
runs:

| | why |
|---|---|
| `SELECT … FOR UPDATE` | stock reservation takes real row locks at checkout |
| `LATERAL` joins | the admin lists aggregate per row in one pass |
| `unnest(…)` | a price-list upload is one statement per five thousand rows |
| `regexp_replace` in a predicate | part numbers match with their separators stripped |
| `word_similarity` | the fuzzy search, which needs pg_trgm |
| `COUNT(*) OVER ()` | the search's exact total, on the same pass as its rows |
| `row_number() OVER (PARTITION BY …)` | the first three specifications *of each part*, not the first three overall |
| `ON CONFLICT … DO NOTHING` | a barcode another part already holds is skipped, not a failed import |

That is a normal way to use EF Core, and better supported than the equivalent
escape hatch in the ORM this project just left.

One thing to know before writing any of it: **every interpolation hole in an
EF raw-SQL string becomes a parameter**, including one holding SQL. A shared
`SELECT` clause spliced into two queries came back as
`syntax error at or near "$1"`. The safety and the inconvenience are the same
property, so that clause is written out twice.

## Checking a port that carries logic

Shape can be compared by sending the same request to both APIs. Logic needs
more, because the interesting failures are not visible in one example: a
rounding half-case, a tie broken the other way, a percentage formatted
differently inside a sentence the customer reads.

The pricing engine is pure on both sides, so `tools/pricing-diff.mjs` pushes
generated inputs through both and compares every field — prices, margins,
clamped discounts, currency, and the applied-rule text. 400 cases, no
differences. The generator is seeded, so a failure can be re-run.

## Progress

**All 66 endpoints are ported.** Every one has been sent the same requests as
the API already serving customers, and answered the same way.

Reads, compared request for request across three sessions each — anonymous, a
retail customer, an admin:

- the catalogue: `/api/systems`, `/api/suppliers`, `/api/suppliers/{slug}`,
  `/api/vehicles`, `/api/vehicles/vin`, `/api/catalog/products/{id}`,
  `/api/catalog/search`, `/api/catalog/bulk`
- the account: `/api/auth/session`, `/api/cart`, `/api/notifications`,
  `/api/orders`
- the admin desk: stats, orders, carts, notifications, clients, products,
  images, stock, suppliers, warehouses, outlets, currencies, tiers, markup
  rules, price lists

Writes, each with a test that creates what it touches and takes it away again:

- `POST`/`PATCH`/`DELETE` on suppliers, warehouses, outlets, currencies,
  client tiers, markup rules, products, price lists
- `PUT` on a product's images and its stock
- `PATCH /api/admin/clients/{id}`, `POST /api/admin/notifications`
- `PATCH /api/admin/orders/{id}` — the status, and the shelves with it
- `PUT /api/cart`, `POST /api/orders`, `POST`/`PATCH /api/notifications`
- `POST /api/auth/login`, `/logout`, `/register`
- `POST /api/suppliers/register` — a supplier signing themselves up, switched off
- `GET /api/admin/suppliers/waiting`, `PATCH /api/admin/suppliers/{id}/approval`

Not endpoints, but what the endpoints are made of: the pricing engine, and
stock reservation — the part LINQ cannot express.

What is left is not porting: the hosting decision. The tests it needs of its
own are done — see below.

## Three things worth naming

**A password hashed by either API is accepted by the other.** They use
different bcrypt libraries, and only one of them had ever written a hash into
this table. An account opened through .NET signs in on Node and the other way
round, and the wrong password is still refused on both.

**Shipping moves stock, and reversing it puts the stock back.** An order shown
as shipped whose units were never drawn down is the discrepancy a warehouse
finds at the next count and cannot explain, so the status and the shelf move in
one transaction. The test walks an order processing → shipped → processing →
shipped → paid and checks the shelf at every step: 20/3, 17/0, 20/3, 17/0,
17/0.

**When the shelf and the order disagree, nothing moves.** Deliberately
arranged: an order holding three units, a shelf edited to say none are
reserved. Shipping would drive `reserved` below zero, the CHECK refuses it, and
both APIs answer 409 with the same sentence — with the status unchanged,
because the update and the stock movement are in the same transaction.

## What each test proves

| script | what it holds down |
|---|---|
| `tools/compare.mjs` | 313 read requests, three sessions each, byte for byte |
| `tools/search-snapshot.mjs` | 228 search responses against their own recorded past |
| `tools/pricing-diff.mjs` | 400 generated pricing cases through both engines |
| `tools/admin-writes.mjs` | 49 admin refusals and round trips |
| `tools/desk-writes.mjs` | 35 price-list, account and notification cases |
| `tools/account-order.mjs` | 29 registration, sign-in and order-status cases |
| `tools/supplier-signup.mjs` | 39 cases, and the eight places a hidden part could leak |
| `tools/order-post.mjs` | the refusals, then one real order, then removed |
| `tools/stock-race.mjs` | two concurrent orders for the last unit; one wins |
| `tools/auth-interop.mjs` | a cookie from either API is accepted by the other |

Five of them write. All five make their own rows, count what was there before
and after, and fail loudly if anything is left behind — a test script in this
project once deleted a real catalogue part because it picked "the first product
in the list" instead of making one. `npm run db:reconcile` in the other
repository is the independent check that no shelf drifted.

Three things the admin desk can create but cannot remove — an order, a
notification, an account — have development-only probes to take them back out.
They are not omissions in the API being worked around; an order is a record of
a sale, a notification is something a person was told, and an account is
somebody's history. The probes exist so a test can make a real one and not
leave it behind.

## How a port is checked

Both APIs read the same database, so the same request can be sent to each and
the responses compared. That is the test: not that the .NET version looks
right, but that it is indistinguishable from the one already serving
customers. Where a response cannot match byte for byte, the difference gets
explained before it gets accepted.

That stops working the moment a change lands on *both* APIs, which is what
happens whenever the port is finished and the product moves on. Two
implementations of the same new mistake agree perfectly, and `compare.mjs`
reports 313/313 while both are wrong. So `tools/search-snapshot.mjs` records
what the search answers *today* — every query shape, as all three accounts —
and checks one API against its own past rather than against its twin. The two
harnesses fail on opposite mistakes, which is the only reason to have both.

It is not proof on its own either. Pushing the search filters into SQL passed
228/228 while quietly breaking one path: the fuzzy fallback reaches its rows by
id, through a query that knows nothing about the filters, and no recorded case
had ever combined a misspelling with a filter. The snapshot could only hold
down the questions somebody had thought to ask. `compare.mjs` caught it,
because only one of the two APIs had been changed yet — and the cases are in
the snapshot now.

## The model, re-scaffolded

`Data/Entities` is reverse-engineered rather than hand-written, and staying
that way is the point: it describes what is actually in the database rather
than what somebody believed was. When the supplier-signup migration added
`Supplier.active`, `Supplier.approvedAt` and `Client.supplierId`, the model was
re-scaffolded rather than edited — the diff came back additive, three columns
and one relation, which is itself the check that nothing had drifted.

Re-scaffolding takes two manual steps afterwards, both worth knowing before
doing it again. `dotnet ef dbcontext scaffold` wants Npgsql's
`Host=…;Database=…` form and rejects a `postgresql://` url — and prints the
whole connection string, password and all, in the error when it does. And it
picks up `_prisma_migrations`, which has to be taken back out of the entities
and the context: it is the migration ledger, the TypeScript runner still owns
it, and modelling it would invite this project to start writing to it.

One table is deliberately absent from `Data/Entities`: `ProductSpec`. Nothing
here writes a specification — the Node importer and the seed do — and every
read of one is the window-function query in `Catalogue/SpecQueries.cs`, which
does not translate to LINQ anyway. A scaffolded entity would carry change
tracking for rows this API never changes. `ProductBarcode` is absent for the same reason, and so are the two
packaging columns on `Product` — which is why the basket reads them in SQL
rather than through the entity. The next re-scaffold will pick all of it up
and that is fine; none of it is needed before then.

## Tests that do not need the other API

`dotnet test` — 314 cases, 143ms, no database and no network. Ported from the
five vitest files in the other repository, which the comparison harness cannot
replace: those run only while the Node API is alive, and the point of a port is
that one day it will not be.

| | |
|---|---|
| `AvailabilityTests` | uncounted and sold-out are different facts and the same answer |
| `SessionTokenTests` | every forgery that has to fail, and the wire format the other API reads |
| `AuthSecretTests` | the three ways a deployment ends up forgeable |
| `RoleTests` | the role column fails closed, and SUPPLIER is not staff |
| `PricingEngineTests` | markup, then discount, then currency, each exactly once |
| `PriceListTests` | the conversion divides, and every refusal names the right reason |
| `ValidatorTests` | what the admin forms may send, and the urls that only look like paths |

Every test in the vitest suite has a counterpart here — checked by name, not by
eye. The extra cases are ones C# needs and TypeScript does not: half-cent
rounding, because .NET rounds half to even and JavaScript rounds half up; a
missing price column, because `Number(undefined)` is NaN and a default of zero
would price a file at nothing; and a body that is not base64 at all, which
`Encode` cannot produce but a browser can send.

**They were checked against being useless.** Two deliberate breakages, both
caught:

| break | caught by |
|---|---|
| rounding half to even instead of half up | 1 test |
| currency conversion multiplying instead of dividing | 4 tests |

A test suite that passes against broken code is worse than no suite, because it
is also a reason not to look.

## What is still not done

Migrations, the seed scripts and the TecDoc importer still live in the other
repository, in TypeScript. That is a deliberate non-decision so far, not an
oversight: they work, they are tested, and nothing here needs to own them yet.

There is no CI here. The other repository has a pipeline; this one has no
remote to run against yet, so `dotnet test` is a thing somebody has to
remember. That is worth fixing on the day this gets pushed somewhere.
