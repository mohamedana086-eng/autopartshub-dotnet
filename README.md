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
GET /health        the process is up
GET /health/db     it can reach the database, and how many products it can see
GET /health/ready  it should be in the rotation: the database answers AND its
                   schema is current. 503 until both hold — which is what a
                   deployment looks like between starting and finishing its
                   migrations. The cache, full text and the mail transport are
                   reported beside them and do not decide.
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
| a stored normalised part number | part numbers match with their separators stripped, and seek |
| `CONTAINSTABLE` and prefix seeks | the near-miss search, where pg_trgm's `word_similarity` was |
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
- the price-list import log: `/api/admin/price-lists/imports`, and
  `/imports/{importId}` for one upload with the lines it could not use —
  every attempt, refusals included, each rejected line carrying its line
  number and the price as the file wrote it. A literal segment beside `{id}`,
  which routing prefers, so both paths still reach what they mean.

Two rules on a price-list upload, ported with the sentences they answer in:

- **a supplier's own numbering is read.** A number matching no part of ours
  falls through to the `Interchange` cross-reference — *exact* equivalents
  only, and an ambiguous one is refused rather than guessed at, because
  picking one of two parts to price is a coin toss nobody can see happening.
  Our own numbers are checked first and always win.
- **a file that moves prices too far is refused with 409.** More than half the
  parts it already prices moving by more than five-fold is the shape of a
  column read from the wrong place, not of a price update. The refusal carries
  the figures that earned it and `confirmLargeChange` sends it through anyway,
  so it is a question rather than a wall. `PriceListTests` asserts the refusal
  sentence in full, character for character, against the same assertion on the
  other side — both APIs put it in front of an admin and into the log.

**A part can be bought from several suppliers.** `SupplierOffer` holds one
supplier's terms for one part, and the `BestOffer` **view** decides which of
them prices it — highest `Supplier.priority`, then lowest price, then the
supplier's code so the answer is stable. With every priority at its default of
zero that is exactly "cheapest wins".

A view rather than the same `ORDER BY` copied into the six queries that price a
row: those six have to agree exactly, and six copies of a ranking is five
chances to change one. The purchase cost is now three rungs — active price
list, then best offer, then `basePrice` — with the list deliberately on top,
because uploading one is the act of saying *"these are the prices now"*.

`OfferPrice` and `OfferSupplierId` are on the **`IPriceable` interface**, so
adding them made the compiler name every query that had not joined the view.
The other API needs a runtime guard for the same job because its rows come out
of casts.

What the compiler cannot see is the SQL. `BestOfferJoinTests` reads every raw
string in the project and asserts that a query reading `bo` joins it, that a
query joining it reads it, and that the join comes **before** anything reading
its alias in another join condition — invalid SQL, and wrong here in two search
queries on the day it was written. It found a third missing join in
`CartEndpoints`.

`/api/admin/products/{id}/offers` manages them, ADMIN only, the whole set per
request like the stock editor. A supplier left off the list does not sell the
part; `active: false` is an offer we are not buying from today. The response is
read back rather than echoed, because which offer wins can change as a
consequence of the edit.

**A supplier's minimum**, `Supplier.minOrderAmount`, read per order at `GET
/api/admin/orders/{id}/suppliers`. Not `ClientCategory.minOrderAmount`, which
is what a customer must spend with us — this is what we must spend with them,
and it is not a checkout rule: the customer cannot see purchase prices, did not
pick the supplier, and could do nothing about a shortfall. It warns whoever
accepts the order, so it is `RequireStaff` and scoped through the order, like
the order list beside it.

Measured in what we pay and at today's cost, both for reasons the other API
spells out in full. This is also the one query where the `BestOffer` join is
not a `LEFT` join — a line no supplier offers is on nobody's purchase order —
and it says so in the SQL, because `BestOfferJoinTests` now insists on the
`LEFT` everywhere a query does not.

**The margin can be stated where the part is bought.** The markup ladder had
three rungs, all of them describing the selling side: a matching rule, the goods
category, the client tier's default. None could say what a buyer says while
working through a supplier's file — "their parts earn 18%, but everything on the
March list earns 22%, except this line at 35%". Three nullable `markupPercent`
columns now carry it, on `Supplier`, `PriceList` and `PriceListItem`, resolved
by `PurchaseMarkups.Of` down a chain of line, then list, then supplier.

Null is not zero and the whole chain turns on it: null means "ask the next
rung", zero means "sell it at cost". The line and the list only answer while the
file in force covers the part, which is the same condition under which that file
sets the cost. Nothing is backfilled, so the day it ships every part prices
exactly as it did.

It sits above the goods category and below a matching rule. That second half is
load-bearing rather than a preference — a rule is the only markup that can see
who is asking, so if a supplier's margin outranked one, typing a number into a
supplier form would silently switch off every customer-specific rule on their
parts.

The supplier margins are loaded once per request into a lookup, the way the
goods categories are: a join would have to be added to all six queries that
price a row, and one of them forgetting it is the silent-wrong-price failure
`BestOfferJoinTests` exists to catch.

**A ladder of standard margins**, `POST /api/admin/markup-rules/ladder`. What
it writes is ordinary markup rules, one per band; there is no ladder table and
nothing marks them as belonging together. The engine could always express this
— each band is a rule with a purchase-price band — so what the endpoint adds is
the shape check: a gap, an overlap, a ladder not starting at zero, or one
closed at the top. Each of those is invisible on a form that writes one rule at
a time and shows up months later as a part priced from the tier default, so the
refusal names the numbers rather than saying the ladder is invalid.

Porting it turned up a divergence worth naming: `MarkupRules.ParseBody` here
refused `PERCENT_MIN` in its type list while the code handling that type's
floor sat below it, unreachable. This port could not create a rule the other
one could, and the two refusals named a different number of types. Both now
read the shared vocabulary.

**Tickets**, `/api/tickets` for the customer and `/api/admin/tickets` for the
queue. The status is derived rather than set — a customer's message opens it,
ours answers it, and a customer writing back to a resolved ticket reopens it —
so the only status anybody sets by hand is `resolved`.

`TicketMessage.internal` is staff writing to each other, on the same table and
in the same thread as the replies, which is what makes one forgotten `WHERE`
send it to the person it is about. Two readers rather than one with a flag,
excluded in the query rather than after it, and the flag not offered on the
customer's side at all. `TicketTests` asserts all three over the source, and
asserts the one rule worth stating twice: **an internal note does not move the
ticket to `answered`**, because staff talking to each other have not answered
anybody.

**The failed-search log**, `SearchMiss`, written from the search endpoint and
read at `GET /api/admin/search-misses`. One row per term rather than per
search, no client recorded at all, and `narrowed` alongside so that *"we do not
sell this"* and *"we do not sell this from that supplier"* stay different
findings. An email address is refused as a term and a long run of digits is
not — it is a part number as often as a phone number. `SearchMissTests` holds
the normalisation to the same answers as the other API, because both write into
the same table and a term normalised two ways is a counter split in two.

**The supplier portal**, `/api/supplier/{summary,orders,stock}`. Its own branch
rather than a corner of `/api/admin`, because a supplier is not staff with
fewer permissions — they are a counterparty, and putting their endpoints beside
the admin ones would mean every future admin route sat one forgotten guard away
from being theirs too.

`SupplierGate` asks three things: signed in, a SUPPLIER account attached to a
supplier, and **that supplier is trading**. An applicant nobody has approved
and a supplier who was stopped this morning are the same state in the schema —
`Supplier.active` is false for both — so neither reads the portal, and the two
are told apart only in the refusal, where the difference is actionable. Which
supplier the account speaks for is read fresh per request rather than carried
in the session, so approval and suspension take effect at once. It is scoped,
not a singleton like `AdminGate`, because it reads the database.

**Two fields never leave**: who bought it (name, id, city, email) and what we
sold it for (`OrderItem.unitPrice`, which embeds the markup). Neither can be
caught by a type — both are ordinary columns on tables the portal already joins
— so `SupplierPortalTests` reads the queries as text and refuses the words,
including `"Client"` itself. It also asserts the comments still *mention* the
forbidden fields, which is what tells "the stripping works" apart from "the
stripping ate the code".

**What a salesperson may change.** A SALES account used to be a scoped
*viewer*: reads narrowed to its own customers, and not one write. Three gates
now, and the third exists to be greppable:

| gate | for | who |
|---|---|---|
| `RequireAdmin` | everything by default | ADMIN |
| `RequireStaff` | reads a salesperson may make | ADMIN, SALES |
| `RequireOperator` | **writes** a salesperson may make | ADMIN, SALES |

`RequireOperator` checks exactly what `RequireStaff` checks, and that is the
point: one gate covering both would make every `RequireStaff` on a GET look
like permission to add a POST beside it. `AdminWriteScopeTests` reads the
endpoint sources and asserts the open list is exactly two — `POST
/api/admin/notifications` and `PATCH /api/admin/orders/{id}` — so opening a
third fails the build until somebody writes it down.

The scope is a condition **inside** the statement, never a check in front of
it: a check in front is one forgotten return away from being no check at all,
and it races anything that moves the row in between. Nothing matched means
nothing changed, the transaction rolls back so the **shelves do not move
either**, and the answer is **404, not 403** — to a salesperson another
manager's order does not exist, and refusing differently would confirm it does.

`PATCH /api/admin/clients/{id}` stays admin-only on purpose: it writes the
role, the pricing tier, the discount, the currency and who owns the account,
and there is no subset of that a salesperson could safely be given.

The order lifecycle, in `Orders/OrderStatuses.cs` — no database in it, so both
APIs read the same answers and both can be tested without one:

- **eight statuses with rules about which may follow which.** An order used to
  have four and no rules at all: any status could be set from any other, so
  `paid` could go back to `order_is_sent` and the shelves would be adjusted
  twice for a shipment that happened once. Rejection is only available *before*
  acceptance — afterwards it is a cancellation, and the two answer to different
  people — and nothing is cancelled after it ships, because goods that have
  left come back as a return.
- **refusing and cancelling have to say why.** Every other status explains
  itself; these two leave a row saying the order did not happen and nothing at
  all saying why. A CHECK carries the same rule.
- **three shelf states, not two.** `holding` / `gone` / `released`, each with a
  position, and a move is the difference between two positions — three numbers
  to get right instead of a case for every pair of statuses. Calling an order
  off is the case the old *has it shipped* question could not express: the
  goods never left, so `quantity` is untouched, but the promise has to end or
  the units stay reserved for an order nobody will ever pick.
- **the list narrows** by status, date range and salesperson. A bare `to` date
  covers the whole of that day, read as UTC; a date that cannot be parsed is
  refused rather than ignored, because a filter that silently does not apply is
  worse than one that fails.

`OrderLifecycleTests` asserts the refusal sentences in full, character for
character, against the same assertions on the other side.

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
| `tools/compare.mjs` | 325 read requests, three sessions each, byte for byte |
| `tools/search-snapshot.mjs` | 228 search responses against their own recorded past |
| `tools/pricing-diff.mjs` | 400 generated pricing cases through both engines |
| `tools/admin-writes.mjs` | 49 admin refusals and round trips |
| `tools/desk-writes.mjs` | 35 price-list, account and notification cases, and the import log an upload leaves behind |
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
tracking for rows this API never changes. `ProductBarcode` and `GoodsCategory` are absent for the same reason, and so
are the packaging columns on `Product`, its `goodsCategoryId`, and the one on
`MarkupRule` — which is why the basket reads them in SQL
rather than through the entity. The next re-scaffold will pick all of it up
and that is fine; none of it is needed before then.

## Tests that do not need the other API

`dotnet test` — 341 cases, 143ms, no database and no network. Ported from the
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
| `PurchaseMarkupTests` | the buying side's own chain, and where it sits in the ladder |
| `BusinessMailTests` | which events are worth a message, and the one that must produce none |
| `MarkupLadderTests` | the four ladder shapes that are refused, and why each matters |
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

**The business messages are here, and so are the account ones now.** `Mail/`
sends what the other API sends when an order is accepted, refused, shipped or
called off, and when a ticket is answered — the same four statuses, the same
silence on the other four, the same sentences word for word.

Password recovery and address confirmation used to be the gap in that sentence,
and they were the only place the storefront could reach a 404 by using the app
normally. They are ported (T-196): `Endpoints/AccountRecoveryEndpoints.cs` over
`Auth/VerificationTokens.cs`, and registration sends a confirmation link again.
Two things there are a contract with the other API rather than a preference,
because both read one `VerificationToken` table and only the hash of a token is
stored — the token encoding and hash (base64url, SHA-256, lowercase hex), and
the word "link" in every refusal about a token, which the reset page matches to
decide whether to offer a fresh one. See CONTRACTS.md.

The mailer was ported ahead of the day it matters, deliberately. Email is a side effect
of a write, not a write: both APIs put the same rows in the same database either
way, so the invariant this port exists to keep — *the two agree on every write* —
was never at risk from the gap. But the day a transport is configured the gap
becomes a real divergence, one API telling a customer their order shipped and
the other saying nothing — and that day is a configuration change rather than a
piece of work anybody schedules time around. The mailer had to be here before
it, not after somebody noticed the mail was intermittent.

`MailTransport.Name` reads the same `MAIL_TRANSPORT` variable by the same rule:
`file` writes an outbox line, anything else that is set is a name nothing
implements, and unset means the file transport in development and a refusal in
production. A deployment that silently wrote mail to a file on the server would
be worse than one that sent nothing, because it would look like it worked. Both
APIs append to the same `.mail/outbox.jsonl`, field for field in the same order
— one outbox is what somebody greps when they ask what went out.

`Mailer.NotifyAsync` swallows and logs, and nothing here calls `SendAsync`;
`BusinessMailTests` asserts both over the source. An admin who pressed "accept"
must not get an error page for an order that was accepted — they would press it
again. The same test asserts the rule that matters more: an internal ticket note
produces no message, checked inside the builder rather than at the endpoint,
because email is a second door out of the room every other guard on that column
is watching.
