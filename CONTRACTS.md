# CONTRACTS.md

Every HTTP call the storefront makes, and what answers it here.

This file exists because the backlog is being built against an API that was
ported endpoint-for-endpoint from a Node original, and "the old API did it this
way" stops being a good enough answer the moment a screen wants something the
old API never had. Anything built from a guess about the contract has to be
built twice. So the contract is written down first, from the two sides that
actually decide it: the Angular calls in `web/src`, and the route handlers in
`AutoPartsHub.Api/Endpoints`.

**It is an inventory, not a proposal.** Every row was read out of the source on
both sides — the handler column is the file and line the route is mapped at,
the caller column is where the request is made. Nothing here is aspirational,
and where the two sides disagree the row says so rather than picking a winner.

## How this file stays true

Regenerate the table with `tools/contracts-inventory.pl`, which walks both
trees and joins them on verb and normalised path (`{id}`, `{slug}` and
`${productId}` all collapse to one placeholder for matching, and the real
spelling is kept for display). A route that gains a caller, loses one, or moves
file shows up as a diff.

The contract test (T-205) is what makes drift break the build. Until it lands,
this file is checked by running the generator and looking at the diff.

## The count

96 routes. **Every route the storefront calls is answered.** One is answered
and not yet called — `POST /api/auth/register-supplier`, which is the first
step of the move described below.

## ~~What the storefront asks for and nothing answers~~ — closed (T-196)

Four routes used to be here, and they were the only place the storefront could
reach a 404 by using the app normally:

| Verb | Path | Called from |
|---|---|---|
| `POST` | `/api/auth/password/forgot` | `pages/forgot-password.page.ts` |
| `POST` | `/api/auth/password/reset` | `pages/reset-password.page.ts` |
| `POST` | `/api/auth/email/confirm` | `pages/confirm-email.page.ts` |
| `POST` | `/api/auth/email/resend` | `shell/confirm-email-banner.ts` |

All four are account recovery and address verification: the screens existed and
were wired, and the handlers had never been ported. They are in
`Endpoints/AccountRecoveryEndpoints.cs` now, over the shared token mechanism in
`Auth/VerificationTokens.cs`.

Registration sends a confirmation link again as well, which the port had also
dropped — without it the banner asking somebody to confirm their address had
nothing to confirm until they pressed "send it again".

**Two things in that flow are a contract rather than a preference**, because
both APIs read one `VerificationToken` table and only the hash of a token is
stored:

- the token encoding — base64url, unpadded, 43 characters — and the hash,
  SHA-256 as lowercase hex. A link mailed by one API is redeemed by whichever
  one the customer's click reaches, and a token hashed with .NET's uppercase
  hex would be refused as invalid while being perfectly valid;
- the word "link" in every refusal about a token, which
  `pages/reset-password.page.ts` matches with `/link/i` to decide whether to
  offer a fresh one.

Both are asserted — `VerificationTokenFormatTests` against values produced by
the other API's own crypto, and `RecoveryMessageTests` against the wording.

## The nine differences the backlog requires

These are not gaps in the implementation — they are places where a working
endpoint has a different shape from the one the new frontend is specified
against. Each will break a screen even though the logic behind it is sound, so
each has to be settled before the screen is built rather than after.

| Area | Today | The backlog asks for |
|---|---|---|
| Supplier sign-up | ~~`POST /api/suppliers/register`~~ — both served | `POST /api/auth/register-supplier` ✅ |
| Failed searches | `GET /api/admin/search-misses` | `GET /api/admin/failed-searches` + `resolve` / `reopen` |
| Cart write | `PUT /api/cart` (whole-basket replace) | `POST` / `PATCH` / `DELETE /api/cart/lines` |
| Bulk body | `partNumbers[]`, 1000 rows | `rows[]` with a manufacturer per row, 2000 rows, 2 MB |
| Order approval | `PATCH /api/admin/orders/{id}` | `POST approve` / `reject` / `cancel`, `PATCH shipping` |
| Supplier portal | `summary` / `orders` / `stock` | + `GET`/`PATCH products`, + `GET manager/scope` |
| Ticket status | `PATCH /api/admin/tickets/{id}` | `PATCH /api/tickets/{id}/status` + `PUT following` |
| Readiness | `/health/db` | `/health/ready` covering SQL Server and Redis |
| Search filter | `partType` | `offerType`, kept separate from `matchIn` |

The sign-up row is the one that has moved. Both paths are served, so the
storefront can change when it changes; dropping `/api/suppliers/register` is
the third step and waits on the caller moving. Everything else in the table is
still as it was.

`PUT /api/cart` is the one worth naming twice. It is a whole-basket replace
because the storefront keeps the basket in `localStorage` and mirrors it — the
endpoint was shaped around a client that owns the state. The line-level
contract is the other way round, and moving to it is a change on both sides at
once (T-171, T-172, T-174).

## Shared vocabulary

The values that travel as strings. An enum serialised as an ordinal would
change meaning the day somebody inserts a value in the middle, so
`Program.cs` registers `JsonStringEnumConverter` and these names are the
contract.

| Set | Values | Where it is fixed |
|---|---|---|
| Role | `ADMIN` `SALES` `SUPPLIER` `B2B` `RETAIL` | `Auth/AuthSecret.cs:70` · `core/api.models.ts:308` |
| `matchedOn` | `part-number` `name` `manufacturer` `interchange-oem` `interchange-aftermarket` `description` | `core/api.models.ts:10` |
| `matchIn` | `part-number` `oem` `aftermarket` | `core/api.models.ts:19` |
| `partType` | `oem` `aftermarket` `substitute` | `core/api.models.ts:28` |
| Sort | `relevance` `price-asc` `price-desc` `delivery` | `core/api.models.ts:40` |
| Packaging unit | `piece` `pair` `set` `box` `pack` `litre` `metre` `kit` | `core/api.models.ts:207` |
| Barcode kind | `ean13` `ean8` `upca` `itf14` `other` | `core/api.models.ts:252` |
| Reliability | `official` `reliable` `standard` | `Catalogue/SearchQueries.cs:634` |

`matchIn` and `partType` share three words and are not the same filter.
Searching an OE number and being sold an aftermarket part is the normal case —
that is what a cross-reference is for. The backlog renames the second to
`offerType` for exactly this reason; until that lands, the two names above are
what the wire carries.

## Supplier identity on the wire

A customer never sees a supplier's name. `SupplierReference.For` is the single
place that decides — staff get `Supplier.Name`, everyone else gets
`Supplier.Code` in the same `name` field, so the response shape does not change
with who is asking and the storefront needs no branch.

Six routes carry a supplier reference and all six go through it:
`/api/catalog/search`, `/api/catalog/products/{id}`, `/api/suppliers`,
`/api/suppliers/{slug}`, `/api/cart`, `/api/catalog/bulk`.
`SupplierAnonymityTests` walks all six as every non-staff role and fails on any
seeded supplier name appearing in a response body.

## The inventory

Handler paths are relative to `AutoPartsHub.Api/`; caller paths to
`web/src/app/`.

| Verb | Path | Handler | Called from |
|---|---|---|---|
| `GET` | `/api/admin/carts` | Endpoints/AdminDeskEndpoints.cs:352 | core/admin.service.ts:346 |
| `GET` | `/api/admin/client-categories` | Endpoints/AdminReferenceEndpoints.cs:143 | core/admin.service.ts:83 |
| `POST` | `/api/admin/client-categories` | Endpoints/AdminPricingWriteEndpoints.cs:146 | core/admin.service.ts:90 |
| `DELETE` | `/api/admin/client-categories/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:188 | core/admin.service.ts:95 |
| `GET` | `/api/admin/clients` | Endpoints/AdminReferenceEndpoints.cs:337 | core/admin.service.ts:31 |
| `PATCH` | `/api/admin/clients/{id}` | Endpoints/AdminDeskWriteEndpoints.cs:26 | core/admin.service.ts:35 |
| `GET` | `/api/admin/currencies` | Endpoints/AdminReferenceEndpoints.cs:121 | core/admin.service.ts:39 |
| `POST` | `/api/admin/currencies` | Endpoints/AdminPricingWriteEndpoints.cs:30 | core/admin.service.ts:43 |
| `DELETE` | `/api/admin/currencies/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:102 | core/admin.service.ts:53 |
| `PATCH` | `/api/admin/currencies/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:57 | core/admin.service.ts:48 |
| `GET` | `/api/admin/goods-categories` | Endpoints/GoodsCategoryEndpoints.cs:28 | core/admin.service.ts:60 |
| `POST` | `/api/admin/goods-categories` | Endpoints/GoodsCategoryEndpoints.cs:38 | core/admin.service.ts:66 |
| `DELETE` | `/api/admin/goods-categories/{id}` | Endpoints/GoodsCategoryEndpoints.cs:108 | core/admin.service.ts:78 |
| `PATCH` | `/api/admin/goods-categories/{id}` | Endpoints/GoodsCategoryEndpoints.cs:68 | core/admin.service.ts:72 |
| `GET` | `/api/admin/markup-rules` | Endpoints/AdminReferenceEndpoints.cs:170 | core/admin.service.ts:99 |
| `POST` | `/api/admin/markup-rules` | Endpoints/AdminPricingWriteEndpoints.cs:231 | core/admin.service.ts:103 |
| `DELETE` | `/api/admin/markup-rules/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:349 | core/admin.service.ts:139 |
| `PATCH` | `/api/admin/markup-rules/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:324 | core/admin.service.ts:134 |
| `PUT` | `/api/admin/markup-rules/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:303 | core/admin.service.ts:128 |
| `POST` | `/api/admin/markup-rules/ladder` | Endpoints/AdminPricingWriteEndpoints.cs:260 | core/admin.service.ts:115 |
| `GET` | `/api/admin/notifications` | Endpoints/AdminDeskEndpoints.cs:446 | core/admin.service.ts:487 |
| `POST` | `/api/admin/notifications` | Endpoints/AdminDeskWriteEndpoints.cs:111 | core/admin.service.ts:492 |
| `GET` | `/api/admin/orders` | Endpoints/AdminDeskEndpoints.cs:209 | core/admin.service.ts:156 |
| `PATCH` | `/api/admin/orders/{id}` | Endpoints/AdminOrderWriteEndpoints.cs:35 | core/admin.service.ts:168 |
| `GET` | `/api/admin/orders/{id}/suppliers` | Endpoints/AdminDeskEndpoints.cs:90 | core/admin.service.ts:179 |
| `GET` | `/api/admin/outlets` | Endpoints/AdminReferenceEndpoints.cs:92 | core/admin.service.ts:326 |
| `POST` | `/api/admin/outlets` | Endpoints/AdminSiteWriteEndpoints.cs:293 | core/admin.service.ts:330 |
| `DELETE` | `/api/admin/outlets/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:359 | core/admin.service.ts:340 |
| `PATCH` | `/api/admin/outlets/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:323 | core/admin.service.ts:335 |
| `GET` | `/api/admin/price-lists` | Endpoints/AdminReferenceEndpoints.cs:192 | core/admin.service.ts:352 |
| `POST` | `/api/admin/price-lists` | Endpoints/AdminPriceListWriteEndpoints.cs:37 | core/admin.service.ts:440 |
| `DELETE` | `/api/admin/price-lists/{id}` | Endpoints/AdminPriceListWriteEndpoints.cs:337 | core/admin.service.ts:481 |
| `GET` | `/api/admin/price-lists/{id}` | Endpoints/AdminReferenceEndpoints.cs:292 | core/admin.service.ts:364 |
| `PATCH` | `/api/admin/price-lists/{id}` | Endpoints/AdminPriceListWriteEndpoints.cs:224 | core/admin.service.ts:456 |
| `PATCH` | `/api/admin/price-lists/{id}/items/{productId}` | Endpoints/AdminPriceListWriteEndpoints.cs:287 | core/admin.service.ts:474 |
| `GET` | `/api/admin/price-lists/imports` | Endpoints/AdminReferenceEndpoints.cs:210 | core/admin.service.ts:424 |
| `GET` | `/api/admin/price-lists/imports/{importId}` | Endpoints/AdminReferenceEndpoints.cs:241 | core/admin.service.ts:434 |
| `GET` | `/api/admin/products` | Endpoints/AdminCatalogueWriteEndpoints.cs:34 | core/admin.service.ts:184 |
| `POST` | `/api/admin/products` | Endpoints/AdminCatalogueWriteEndpoints.cs:110 | core/admin.service.ts:188 |
| `DELETE` | `/api/admin/products/{id}` | Endpoints/AdminCatalogueWriteEndpoints.cs:197 | core/admin.service.ts:198 |
| `PATCH` | `/api/admin/products/{id}` | Endpoints/AdminCatalogueWriteEndpoints.cs:150 | core/admin.service.ts:193 |
| `GET` | `/api/admin/products/{id}/images` | Endpoints/AdminCatalogueWriteEndpoints.cs:236 | core/admin.service.ts:244 |
| `PUT` | `/api/admin/products/{id}/images` | Endpoints/AdminCatalogueWriteEndpoints.cs:250 | core/admin.service.ts:251 |
| `GET` | `/api/admin/products/{id}/offers` | Endpoints/AdminOfferEndpoints.cs:29 | core/admin.service.ts:275 |
| `PUT` | `/api/admin/products/{id}/offers` | Endpoints/AdminOfferEndpoints.cs:53 | core/admin.service.ts:293 |
| `GET` | `/api/admin/products/{id}/stock` | Endpoints/AdminCatalogueWriteEndpoints.cs:286 | core/admin.service.ts:259 |
| `PUT` | `/api/admin/products/{id}/stock` | Endpoints/AdminCatalogueWriteEndpoints.cs:300 | core/admin.service.ts:266 |
| `GET` | `/api/admin/search-misses` | Endpoints/AdminDeskEndpoints.cs:157 | core/admin.service.ts:414 |
| `GET` | `/api/admin/stats` | Endpoints/AdminDeskEndpoints.cs:33 | core/admin.service.ts:27 |
| `GET` | `/api/admin/suppliers` | Endpoints/AdminReferenceEndpoints.cs:26 | core/admin.service.ts:202 |
| `POST` | `/api/admin/suppliers` | Endpoints/AdminSiteWriteEndpoints.cs:30 | core/admin.service.ts:206 |
| `DELETE` | `/api/admin/suppliers/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:153 | core/admin.service.ts:237 |
| `PATCH` | `/api/admin/suppliers/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:68 | core/admin.service.ts:211<br>core/admin.service.ts:222<br>core/admin.service.ts:232 |
| `PATCH` | `/api/admin/suppliers/{id}/approval` | Endpoints/SupplierSignupEndpoints.cs:250 | core/admin.service.ts:518 |
| `GET` | `/api/admin/suppliers/waiting` | Endpoints/SupplierSignupEndpoints.cs:194 | core/admin.service.ts:500 |
| `GET` | `/api/admin/tickets` | Endpoints/TicketEndpoints.cs:171 | core/admin.service.ts:376 |
| `GET` | `/api/admin/tickets/{id}` | Endpoints/TicketEndpoints.cs:230 | core/admin.service.ts:381 |
| `PATCH` | `/api/admin/tickets/{id}` | Endpoints/TicketEndpoints.cs:262 | core/admin.service.ts:400 |
| `POST` | `/api/admin/tickets/{id}/messages` | Endpoints/TicketEndpoints.cs:288 | core/admin.service.ts:393 |
| `GET` | `/api/admin/warehouses` | Endpoints/AdminReferenceEndpoints.cs:61 | core/admin.service.ts:302 |
| `POST` | `/api/admin/warehouses` | Endpoints/AdminSiteWriteEndpoints.cs:193 | core/admin.service.ts:307 |
| `DELETE` | `/api/admin/warehouses/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:248 | core/admin.service.ts:319 |
| `PATCH` | `/api/admin/warehouses/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:217 | core/admin.service.ts:313 |
| `POST` | `/api/auth/email/confirm` | Endpoints/AccountRecoveryEndpoints.cs:148 | pages/confirm-email.page.ts:59 |
| `POST` | `/api/auth/email/resend` | Endpoints/AccountRecoveryEndpoints.cs:195 | shell/confirm-email-banner.ts:67 |
| `POST` | `/api/auth/login` | Endpoints/AuthEndpoints.cs:16 | core/auth.service.ts:57 |
| `POST` | `/api/auth/logout` | Endpoints/AuthEndpoints.cs:137 | core/auth.service.ts:72 |
| `POST` | `/api/auth/password/forgot` | Endpoints/AccountRecoveryEndpoints.cs:38 | pages/forgot-password.page.ts:80 |
| `POST` | `/api/auth/password/reset` | Endpoints/AccountRecoveryEndpoints.cs:90 | pages/reset-password.page.ts:101 |
| `POST` | `/api/auth/register` | Endpoints/AuthEndpoints.cs:59 | core/auth.service.ts:65 |
| `POST` | `/api/auth/register-supplier` | Endpoints/SupplierSignupEndpoints.cs:47 | _none_ |
| `GET` | `/api/auth/session` | Endpoints/AuthEndpoints.cs:148 | core/auth.service.ts:46 |
| `GET` | `/api/cart` | Endpoints/CartEndpoints.cs:32 | core/cart.service.ts:260 |
| `PUT` | `/api/cart` | Endpoints/CartEndpoints.cs:49 | core/cart.service.ts:235 |
| `POST` | `/api/catalog/bulk` | Endpoints/BulkLookupEndpoints.cs:26 | pages/bulk.page.ts:258 |
| `GET` | `/api/catalog/products/{id}` | Endpoints/ProductEndpoints.cs:13 | core/catalog.service.ts:67 |
| `GET` | `/api/catalog/search` | Endpoints/SearchEndpoints.cs:58 | core/catalog.service.ts:53 |
| `GET` | `/api/notifications` | Endpoints/NotificationEndpoints.cs:16 | core/notifications.service.ts:45 |
| `POST` | `/api/notifications` | Endpoints/NotificationEndpoints.cs:55 | core/notifications.service.ts:83 |
| `PATCH` | `/api/notifications/{id}` | Endpoints/NotificationEndpoints.cs:77 | core/notifications.service.ts:70 |
| `GET` | `/api/orders` | Endpoints/OrderEndpoints.cs:20 | core/orders.service.ts:41 |
| `POST` | `/api/orders` | Endpoints/OrderEndpoints.cs:95 | core/orders.service.ts:37 |
| `GET` | `/api/supplier/orders` | Endpoints/SupplierPortalEndpoints.cs:109 | core/supplier.service.ts:67 |
| `GET` | `/api/supplier/stock` | Endpoints/SupplierPortalEndpoints.cs:163 | core/supplier.service.ts:73 |
| `GET` | `/api/supplier/summary` | Endpoints/SupplierPortalEndpoints.cs:62 | core/supplier.service.ts:61 |
| `GET` | `/api/suppliers` | Endpoints/CatalogueEndpoints.cs:35 | core/suppliers.service.ts:32 |
| `GET` | `/api/suppliers/{slug}` | Endpoints/SupplierPageEndpoints.cs:22 | core/suppliers.service.ts:36 |
| `POST` | `/api/suppliers/register` | Endpoints/SupplierSignupEndpoints.cs:48 | pages/supplier-register.page.ts:159 |
| `GET` | `/api/systems` | Endpoints/CatalogueEndpoints.cs:23 | core/catalog.service.ts:11 |
| `GET` | `/api/tickets` | Endpoints/TicketEndpoints.cs:39 | core/support.service.ts:19 |
| `POST` | `/api/tickets` | Endpoints/TicketEndpoints.cs:64 | core/support.service.ts:28 |
| `GET` | `/api/tickets/{id}` | Endpoints/TicketEndpoints.cs:98 | core/support.service.ts:24 |
| `POST` | `/api/tickets/{id}/messages` | Endpoints/TicketEndpoints.cs:136 | core/support.service.ts:33 |
| `GET` | `/api/vehicles` | Endpoints/VehicleEndpoints.cs:15 | core/vehicles.service.ts:58 |
| `GET` | `/api/vehicles/find` | Endpoints/VehicleFinderEndpoints.cs:24 | pages/vehicle-finder.page.ts:221 |
| `GET` | `/api/vehicles/vin` | Endpoints/VehicleEndpoints.cs:25 | core/vehicles.service.ts:64 |
