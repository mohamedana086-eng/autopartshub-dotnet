using AutoPartsHub.Api;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Endpoints;
using AutoPartsHub.Api.Pricing;
using AutoPartsHub.Application.Abstractions;
using AutoPartsHub.Infrastructure.Local;
using Microsoft.EntityFrameworkCore;

// The catalogue is ordered with culture-aware comparisons, because that is
// what the API this one replaces does: JavaScript's localeCompare treats case
// as a secondary weight, so an ordinal sort puts "CV joint kit" before "Cabin
// filter" and a customer's results come back shuffled. That bug has been
// fixed here once already.
//
// Globalization-invariant mode turns every InvariantCulture comparison back
// into an ordinal one without saying so, and several slim container images
// turn it on by shipping without ICU. Nothing downstream would notice — the
// app starts, the health check passes, and the search quietly sorts wrong —
// so it is refused here instead.
if (AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) && invariant)
{
    throw new InvalidOperationException(
        "Globalization-invariant mode is on, which would reorder the catalogue. "
        + "The image needs ICU: use the Debian-based runtime, or the -extra "
        + "variant of a chiseled one.");
}

var builder = WebApplication.CreateBuilder(args);

// Hosts that choose the port announce it in PORT — Railway, Render, Cloud Run
// and Heroku all do, and none of them read EXPOSE. Kestrel does not look at
// PORT, so the two are joined up here rather than in a shell wrapper around
// the entrypoint.
//
// Precedence is ASPNETCORE_URLS, then PORT. Nothing else may sit in between:
// the Dockerfile deliberately does not set ASPNETCORE_HTTP_PORTS, because a
// default baked into the image would outrank the port the host actually
// assigned and the container would listen where nobody is looking. It sets
// PORT instead, which a host overrides simply by setting its own.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// Before anything reads configuration. In a deployment there is no file and
// this does nothing; locally it is what makes `dotnet run` work unattended.
if (builder.Environment.IsDevelopment())
{
    DotEnv.Load(builder.Environment.ContentRootPath);
}

builder.Services.AddOpenApi();

// Enums travel as their names, not their ordinals. The storefront is
// TypeScript and reads "PERCENT", and an ordinal would silently change meaning
// the day someone inserts a value into the middle of an enum.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

// One instance, holding the resolved key. Resolved at startup rather than at
// first use: unlike the Node build, nothing here compiles under a production
// environment without also running, so there is no build to keep working.
builder.Services.AddSingleton(new SessionTokens(
    AuthSecret.Resolve(Environment.GetEnvironmentVariable("AUTH_SECRET"), builder.Environment.IsProduction())));

builder.Services.AddScoped<PricingContextLoader>();
builder.Services.AddScoped<SearchQueries>();
builder.Services.AddScoped<SpecQueries>();
builder.Services.AddScoped<AutoPartsHub.Api.Vehicles.VehicleFinder>();
builder.Services.AddScoped<BarcodeQueries>();
builder.Services.AddSingleton<AdminGate>();
// The mailer. Singleton because it holds nothing but a logger, and because
// what it writes — a line in an outbox, or a line in the log saying why not —
// has nothing per-request about it.
builder.Services.AddSingleton<AutoPartsHub.Api.Mail.Mailer>();
// Scoped, not singleton: unlike AdminGate it reads the database, so it takes
// the request's DbContext.
builder.Services.AddScoped<SupplierGate>();
// Refuses an id that arrives in a request body and names something outside
// the caller's scope. Scoped for the same reason SupplierGate is. The ids in
// a route are not its business — those are narrowed inside the statement that
// writes them, which is stronger. See IScopeGuard.
builder.Services.AddScoped<IScopeGuard, ScopeGuard>();

// The integrations, behind interfaces, with local implementations bound here.
//
// Every one of these is a thing this deployment does not have: there is no
// TecDoc subscription, no Odoo, no Redis and no SMTP server. The point of
// naming them now is that acquiring one becomes a line in this block plus a
// class in Infrastructure, rather than a change to the code that uses it —
// and until then the API and the worker run with none of them, which is what
// makes a checkout testable on a laptop.
//
// Bound unconditionally rather than under IsDevelopment(). A production
// binding that silently differs from the one every test runs against is how a
// deployment develops behaviour nobody has exercised; when a real adapter
// exists it replaces the line, and the fake stops being reachable at all.
builder.Services.AddSingleton<IPriceCache, InMemoryPriceCache>();
builder.Services.AddSingleton<ITecDocClient, FakeTecDocClient>();
builder.Services.AddSingleton<IOdooClient, FakeOdooClient>();
// Outside the content root, not under it: a file whose contents the caller
// chose, served back from this origin, is stored cross-site scripting.
builder.Services.AddSingleton<IFileStore>(_ => new LocalDiskFileStore(
    Path.Combine(builder.Environment.ContentRootPath, "..", ".uploads")));
// The real one — it writes the file outbox, or refuses in production, exactly
// as it did before there was an interface in front of it.
builder.Services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<AutoPartsHub.Api.Mail.Mailer>());
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

builder.Services.AddDbContext<AutoPartsContext>(options =>
    options.UseNpgsql(ConnectionString.Resolve(builder.Configuration)));

// The storefront is served from its own origin and calls this one, so the
// browser has to be told that is allowed — and with credentials, because the
// session travels as a cookie. Origins come from configuration rather than a
// wildcard: AllowAnyOrigin and AllowCredentials are not permitted together,
// and a wildcard would be the wrong answer here regardless.
const string StorefrontCors = "storefront";
builder.Services.AddCors(options => options.AddPolicy(StorefrontCors, policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Prices with whatever rules the caller sends — which is what the admin's
    // markup screen does, and what no anonymous caller may. Development only.
    // It is here to be compared against the TypeScript engine, field by field.
    app.MapPricingProbe();

    // Holds stock with no order behind it, so the locking can be raced.
    app.MapStockProbe();

    // Deletes an order and gives back the stock it held, so a test can place
    // a real one and not leave it behind.
    app.MapOrderProbe();

    // Likewise for a notification and an account: neither API can unsend one
    // or close the other, so the tests that make real ones need a way back.
    app.MapDeskProbes();
}

app.UseCors(StorefrontCors);

// After CORS, so a preflight is answered before anything is refused — a
// browser that never gets its OPTIONS answered never sends the request the
// token would have been on. Before every endpoint, including the health checks
// and the dev probes: a route that wants out has to say so here.
app.UseMiddleware<CsrfMiddleware>();

// After CSRF, so a request that is refused for both is refused for the reason
// it would be refused for anyway once it has a token — and so that the token
// cookie is still issued to a customer who wandered onto an admin URL.
//
// The second lock on the admin routes. Every one of them gates itself and a
// test says so; this is what holds when a handler stops doing it. See
// AdminRouteGuard for why a source-level assertion is not enough on its own.
app.UseMiddleware<AdminRouteGuard>();

// Liveness and readiness kept apart on purpose: a host that restarts the
// container because the database blinked turns a brief outage into a longer one.
app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/health/db", async (AutoPartsContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Ok(new { ok = true, products = await db.Products.CountAsync() })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapCatalogueEndpoints();
app.MapSupplierPageEndpoints();
app.MapSupplierSignupEndpoints();
app.MapBulkLookupEndpoints();
app.MapVehicleEndpoints();
app.MapAuthEndpoints();
app.MapProductEndpoints();
app.MapSearchEndpoints();
app.MapGoodsCategoryEndpoints();
app.MapVehicleFinderEndpoints();
app.MapCartEndpoints();
app.MapNotificationEndpoints();
app.MapOrderEndpoints();
app.MapAdminDeskEndpoints();
app.MapAdminReferenceEndpoints();
app.MapAdminSiteWriteEndpoints();
app.MapAdminPricingWriteEndpoints();
app.MapAdminPriceListWriteEndpoints();
app.MapAdminCatalogueWriteEndpoints();
app.MapAdminDeskWriteEndpoints();
app.MapSupplierPortalEndpoints();
app.MapTicketEndpoints();
app.MapAdminOfferEndpoints();
app.MapAdminOrderWriteEndpoints();

app.Run();
