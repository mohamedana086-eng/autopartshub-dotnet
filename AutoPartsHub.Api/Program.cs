using AutoPartsHub.Api;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Endpoints;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Pricing;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

    // Likewise for a notification: neither API can unsend one, so a test that
    // sends a real message needs a way to take it back out.
    app.MapNotificationProbe();
}

app.UseCors(StorefrontCors);

// Liveness and readiness kept apart on purpose: a host that restarts the
// container because the database blinked turns a brief outage into a longer one.
app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/health/db", async (AutoPartsContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Ok(new { ok = true, products = await db.Products.CountAsync() })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapCatalogueEndpoints();
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

app.Run();
