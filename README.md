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

EF Core covers ordinary CRUD. These do not translate, and will be written as
raw SQL through `FromSql`, the same statements the Node API already runs:

| | why |
|---|---|
| `SELECT … FOR UPDATE` | stock reservation takes real row locks at checkout |
| `LATERAL` joins | the admin lists aggregate per row in one pass |
| `ON CONFLICT` | every seed and upsert |
| `unnest(…)` | a price-list upload is one statement per five thousand rows |
| `word_similarity` | the fuzzy search, which needs pg_trgm |

That is a normal way to use EF Core, and better supported than the equivalent
escape hatch in the ORM this project just left.

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

Ported and verified against the live Node API:

- `GET /api/systems`
- `GET /api/suppliers`
- `GET /api/vehicles`
- `GET /api/vehicles/vin`
- `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/session`
- the pricing engine — not an endpoint, but what every priced response calls
- stock reservation — likewise, and the part LINQ cannot express
- `GET /api/catalog/search` — 105 filter combinations compared
- `GET`/`PUT /api/cart`
- `GET`/`POST /api/notifications`, `PATCH /api/notifications/{id}`

Remaining: 52 of 63. What is left is CRUD on patterns already settled: the
admin lists, the basket, orders and notifications.

## How a port is checked

Both APIs read the same database, so the same request can be sent to each and
the responses compared. That is the test: not that the .NET version looks
right, but that it is indistinguishable from the one already serving
customers. Where a response cannot match byte for byte, the difference gets
explained before it gets accepted.
