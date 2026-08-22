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
  configuration from environment variables and will ship with a Dockerfile, so
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

**All 63 endpoints are ported.** Every one has been sent the same requests as
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

Not endpoints, but what the endpoints are made of: the pricing engine, and
stock reservation — the part LINQ cannot express.

What is left is not porting: a Dockerfile, and the hosting decision.

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
| `tools/compare.mjs` | 232 read requests, three sessions each, byte for byte |
| `tools/pricing-diff.mjs` | 400 generated pricing cases through both engines |
| `tools/admin-writes.mjs` | 49 admin refusals and round trips |
| `tools/desk-writes.mjs` | 35 price-list, account and notification cases |
| `tools/account-order.mjs` | 29 registration, sign-in and order-status cases |
| `tools/order-post.mjs` | the refusals, then one real order, then removed |
| `tools/stock-race.mjs` | two concurrent orders for the last unit; one wins |
| `tools/auth-interop.mjs` | a cookie from either API is accepted by the other |

Four of them write. All four make their own rows, count what was there before
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
