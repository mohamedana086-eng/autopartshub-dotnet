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

96 routes. Four are called by the storefront and answered by nothing. One is
answered and not yet called — `POST /api/auth/register-supplier`, which is the
first step of the move described below.

## What the storefront asks for and nothing answers

| Verb | Path | Called from | Backlog task |
|---|---|---|---|
| `POST` | `/api/auth/password/forgot` | `pages/forgot-password.page.ts:80` | T-196 |
| `POST` | `/api/auth/password/reset` | `pages/reset-password.page.ts:101` | T-196 |
| `POST` | `/api/auth/email/confirm` | `pages/confirm-email.page.ts:59` | T-196 |
| `POST` | `/api/auth/email/resend` | `shell/confirm-email-banner.ts:67` | T-196 |

All four are account recovery and address verification. The screens exist and
are wired; the handlers were never ported. `README.md` says as much. They are
the only place the storefront can reach a 404 by using the app normally.

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
| `GET` | `/api/admin/carts` | Endpoints/AdminDeskEndpoints.cs:351 | core/admin.service.ts:346 |
| `GET` | `/api/admin/client-categories` | Endpoints/AdminReferenceEndpoints.cs:142 | core/admin.service.ts:83 |
| `POST` | `/api/admin/client-categories` | Endpoints/AdminPricingWriteEndpoints.cs:143 | core/admin.service.ts:90 |
| `DELETE` | `/api/admin/client-categories/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:185 | core/admin.service.ts:95 |
| `GET` | `/api/admin/clients` | Endpoints/AdminReferenceEndpoints.cs:336 | core/admin.service.ts:31 |
| `PATCH` | `/api/admin/clients/{id}` | Endpoints/AdminDeskWriteEndpoints.cs:24 | core/admin.service.ts:35 |
| `GET` | `/api/admin/currencies` | Endpoints/AdminReferenceEndpoints.cs:120 | core/admin.service.ts:39 |
| `POST` | `/api/admin/currencies` | Endpoints/AdminPricingWriteEndpoints.cs:27 | core/admin.service.ts:43 |
| `DELETE` | `/api/admin/currencies/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:99 | core/admin.service.ts:53 |
| `PATCH` | `/api/admin/currencies/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:54 | core/admin.service.ts:48 |
| `GET` | `/api/admin/goods-categories` | Endpoints/GoodsCategoryEndpoints.cs:26 | core/admin.service.ts:60 |
| `POST` | `/api/admin/goods-categories` | Endpoints/GoodsCategoryEndpoints.cs:36 | core/admin.service.ts:66 |
| `DELETE` | `/api/admin/goods-categories/{id}` | Endpoints/GoodsCategoryEndpoints.cs:106 | core/admin.service.ts:78 |
| `PATCH` | `/api/admin/goods-categories/{id}` | Endpoints/GoodsCategoryEndpoints.cs:66 | core/admin.service.ts:72 |
| `GET` | `/api/admin/markup-rules` | Endpoints/AdminReferenceEndpoints.cs:169 | core/admin.service.ts:99 |
| `POST` | `/api/admin/markup-rules` | Endpoints/AdminPricingWriteEndpoints.cs:228 | core/admin.service.ts:103 |
| `DELETE` | `/api/admin/markup-rules/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:346 | core/admin.service.ts:139 |
| `PATCH` | `/api/admin/markup-rules/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:321 | core/admin.service.ts:134 |
| `PUT` | `/api/admin/markup-rules/{id}` | Endpoints/AdminPricingWriteEndpoints.cs:300 | core/admin.service.ts:128 |
| `POST` | `/api/admin/markup-rules/ladder` | Endpoints/AdminPricingWriteEndpoints.cs:257 | core/admin.service.ts:115 |
| `GET` | `/api/admin/notifications` | Endpoints/AdminDeskEndpoints.cs:446 | core/admin.service.ts:487 |
| `POST` | `/api/admin/notifications` | Endpoints/AdminDeskWriteEndpoints.cs:109 | core/admin.service.ts:492 |
| `GET` | `/api/admin/orders` | Endpoints/AdminDeskEndpoints.cs:208 | core/admin.service.ts:156 |
| `PATCH` | `/api/admin/orders/{id}` | Endpoints/AdminOrderWriteEndpoints.cs:36 | core/admin.service.ts:168 |
| `GET` | `/api/admin/orders/{id}/suppliers` | Endpoints/AdminDeskEndpoints.cs:89 | core/admin.service.ts:179 |
| `GET` | `/api/admin/outlets` | Endpoints/AdminReferenceEndpoints.cs:91 | core/admin.service.ts:326 |
| `POST` | `/api/admin/outlets` | Endpoints/AdminSiteWriteEndpoints.cs:290 | core/admin.service.ts:330 |
| `DELETE` | `/api/admin/outlets/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:356 | core/admin.service.ts:340 |
| `PATCH` | `/api/admin/outlets/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:320 | core/admin.service.ts:335 |
| `GET` | `/api/admin/price-lists` | Endpoints/AdminReferenceEndpoints.cs:191 | core/admin.service.ts:352 |
| `POST` | `/api/admin/price-lists` | Endpoints/AdminPriceListWriteEndpoints.cs:34 | core/admin.service.ts:440 |
| `DELETE` | `/api/admin/price-lists/{id}` | Endpoints/AdminPriceListWriteEndpoints.cs:334 | core/admin.service.ts:481 |
| `GET` | `/api/admin/price-lists/{id}` | Endpoints/AdminReferenceEndpoints.cs:291 | core/admin.service.ts:364 |
| `PATCH` | `/api/admin/price-lists/{id}` | Endpoints/AdminPriceListWriteEndpoints.cs:221 | core/admin.service.ts:456 |
| `PATCH` | `/api/admin/price-lists/{id}/items/{productId}` | Endpoints/AdminPriceListWriteEndpoints.cs:284 | core/admin.service.ts:474 |
| `GET` | `/api/admin/price-lists/imports` | Endpoints/AdminReferenceEndpoints.cs:209 | core/admin.service.ts:424 |
| `GET` | `/api/admin/price-lists/imports/{importId}` | Endpoints/AdminReferenceEndpoints.cs:240 | core/admin.service.ts:434 |
| `GET` | `/api/admin/products` | Endpoints/AdminCatalogueWriteEndpoints.cs:33 | core/admin.service.ts:184 |
| `POST` | `/api/admin/products` | Endpoints/AdminCatalogueWriteEndpoints.cs:109 | core/admin.service.ts:188 |
| `DELETE` | `/api/admin/products/{id}` | Endpoints/AdminCatalogueWriteEndpoints.cs:196 | core/admin.service.ts:198 |
| `PATCH` | `/api/admin/products/{id}` | Endpoints/AdminCatalogueWriteEndpoints.cs:149 | core/admin.service.ts:193 |
| `GET` | `/api/admin/products/{id}/images` | Endpoints/AdminCatalogueWriteEndpoints.cs:235 | core/admin.service.ts:244 |
| `PUT` | `/api/admin/products/{id}/images` | Endpoints/AdminCatalogueWriteEndpoints.cs:249 | core/admin.service.ts:251 |
| `GET` | `/api/admin/products/{id}/offers` | Endpoints/AdminOfferEndpoints.cs:28 | core/admin.service.ts:275 |
| `PUT` | `/api/admin/products/{id}/offers` | Endpoints/AdminOfferEndpoints.cs:52 | core/admin.service.ts:293 |
| `GET` | `/api/admin/products/{id}/stock` | Endpoints/AdminCatalogueWriteEndpoints.cs:285 | core/admin.service.ts:259 |
| `PUT` | `/api/admin/products/{id}/stock` | Endpoints/AdminCatalogueWriteEndpoints.cs:299 | core/admin.service.ts:266 |
| `GET` | `/api/admin/search-misses` | Endpoints/AdminDeskEndpoints.cs:156 | core/admin.service.ts:414 |
| `GET` | `/api/admin/stats` | Endpoints/AdminDeskEndpoints.cs:32 | core/admin.service.ts:27 |
| `GET` | `/api/admin/suppliers` | Endpoints/AdminReferenceEndpoints.cs:25 | core/admin.service.ts:202 |
| `POST` | `/api/admin/suppliers` | Endpoints/AdminSiteWriteEndpoints.cs:27 | core/admin.service.ts:206 |
| `DELETE` | `/api/admin/suppliers/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:150 | core/admin.service.ts:237 |
| `PATCH` | `/api/admin/suppliers/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:65 | core/admin.service.ts:211<br>core/admin.service.ts:222<br>core/admin.service.ts:232 |
| `PATCH` | `/api/admin/suppliers/{id}/approval` | Endpoints/SupplierSignupEndpoints.cs:238 | core/admin.service.ts:518 |
| `GET` | `/api/admin/suppliers/waiting` | Endpoints/SupplierSignupEndpoints.cs:192 | core/admin.service.ts:500 |
| `GET` | `/api/admin/tickets` | Endpoints/TicketEndpoints.cs:172 | core/admin.service.ts:376 |
| `GET` | `/api/admin/tickets/{id}` | Endpoints/TicketEndpoints.cs:231 | core/admin.service.ts:381 |
| `PATCH` | `/api/admin/tickets/{id}` | Endpoints/TicketEndpoints.cs:263 | core/admin.service.ts:400 |
| `POST` | `/api/admin/tickets/{id}/messages` | Endpoints/TicketEndpoints.cs:289 | core/admin.service.ts:393 |
| `GET` | `/api/admin/warehouses` | Endpoints/AdminReferenceEndpoints.cs:60 | core/admin.service.ts:302 |
| `POST` | `/api/admin/warehouses` | Endpoints/AdminSiteWriteEndpoints.cs:190 | core/admin.service.ts:307 |
| `DELETE` | `/api/admin/warehouses/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:245 | core/admin.service.ts:319 |
| `PATCH` | `/api/admin/warehouses/{id}` | Endpoints/AdminSiteWriteEndpoints.cs:214 | core/admin.service.ts:313 |
| `POST` | `/api/auth/email/confirm` | **absent** | pages/confirm-email.page.ts:59 |
| `POST` | `/api/auth/email/resend` | **absent** | shell/confirm-email-banner.ts:67 |
| `POST` | `/api/auth/login` | Endpoints/AuthEndpoints.cs:13 | core/auth.service.ts:57 |
| `POST` | `/api/auth/logout` | Endpoints/AuthEndpoints.cs:116 | core/auth.service.ts:72 |
| `POST` | `/api/auth/password/forgot` | **absent** | pages/forgot-password.page.ts:80 |
| `POST` | `/api/auth/password/reset` | **absent** | pages/reset-password.page.ts:101 |
| `POST` | `/api/auth/register` | Endpoints/AuthEndpoints.cs:56 | core/auth.service.ts:65 |
| `POST` | `/api/auth/register-supplier` | Endpoints/SupplierSignupEndpoints.cs:45 | _none_ |
| `GET` | `/api/auth/session` | Endpoints/AuthEndpoints.cs:127 | core/auth.service.ts:46 |
| `GET` | `/api/cart` | Endpoints/CartEndpoints.cs:30 | core/cart.service.ts:260 |
| `PUT` | `/api/cart` | Endpoints/CartEndpoints.cs:47 | core/cart.service.ts:235 |
| `POST` | `/api/catalog/bulk` | Endpoints/BulkLookupEndpoints.cs:24 | pages/bulk.page.ts:258 |
| `GET` | `/api/catalog/products/{id}` | Endpoints/ProductEndpoints.cs:13 | core/catalog.service.ts:67 |
| `GET` | `/api/catalog/search` | Endpoints/SearchEndpoints.cs:57 | core/catalog.service.ts:53 |
| `GET` | `/api/notifications` | Endpoints/NotificationEndpoints.cs:16 | core/notifications.service.ts:45 |
| `POST` | `/api/notifications` | Endpoints/NotificationEndpoints.cs:55 | core/notifications.service.ts:83 |
| `PATCH` | `/api/notifications/{id}` | Endpoints/NotificationEndpoints.cs:77 | core/notifications.service.ts:70 |
| `GET` | `/api/orders` | Endpoints/OrderEndpoints.cs:19 | core/orders.service.ts:41 |
| `POST` | `/api/orders` | Endpoints/OrderEndpoints.cs:94 | core/orders.service.ts:37 |
| `GET` | `/api/supplier/orders` | Endpoints/SupplierPortalEndpoints.cs:108 | core/supplier.service.ts:67 |
| `GET` | `/api/supplier/stock` | Endpoints/SupplierPortalEndpoints.cs:162 | core/supplier.service.ts:73 |
| `GET` | `/api/supplier/summary` | Endpoints/SupplierPortalEndpoints.cs:61 | core/supplier.service.ts:61 |
| `GET` | `/api/suppliers` | Endpoints/CatalogueEndpoints.cs:35 | core/suppliers.service.ts:32 |
| `GET` | `/api/suppliers/{slug}` | Endpoints/SupplierPageEndpoints.cs:22 | core/suppliers.service.ts:36 |
| `POST` | `/api/suppliers/register` | Endpoints/SupplierSignupEndpoints.cs:46 | pages/supplier-register.page.ts:159 |
| `GET` | `/api/systems` | Endpoints/CatalogueEndpoints.cs:23 | core/catalog.service.ts:11 |
| `GET` | `/api/tickets` | Endpoints/TicketEndpoints.cs:40 | core/support.service.ts:19 |
| `POST` | `/api/tickets` | Endpoints/TicketEndpoints.cs:65 | core/support.service.ts:28 |
| `GET` | `/api/tickets/{id}` | Endpoints/TicketEndpoints.cs:99 | core/support.service.ts:24 |
| `POST` | `/api/tickets/{id}/messages` | Endpoints/TicketEndpoints.cs:137 | core/support.service.ts:33 |
| `GET` | `/api/vehicles` | Endpoints/VehicleEndpoints.cs:14 | core/vehicles.service.ts:58 |
| `GET` | `/api/vehicles/find` | Endpoints/VehicleFinderEndpoints.cs:23 | pages/vehicle-finder.page.ts:221 |
| `GET` | `/api/vehicles/vin` | Endpoints/VehicleEndpoints.cs:24 | core/vehicles.service.ts:64 |
