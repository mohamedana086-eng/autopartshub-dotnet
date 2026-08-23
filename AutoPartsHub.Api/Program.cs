using AutoPartsHub.Api;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Endpoints;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Pricing;
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
builder.Services.AddScoped<BarcodeQueries>();
builder.Services.AddSingleton<AdminGate>();

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
app.MapAdminOrderWriteEndpoints();

app.Run();
