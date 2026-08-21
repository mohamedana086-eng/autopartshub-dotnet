using AutoPartsHub.Api;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Before anything reads configuration. In a deployment there is no file and
// this does nothing; locally it is what makes `dotnet run` work unattended.
if (builder.Environment.IsDevelopment())
{
    DotEnv.Load(builder.Environment.ContentRootPath);
}

builder.Services.AddOpenApi();

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

app.Run();
