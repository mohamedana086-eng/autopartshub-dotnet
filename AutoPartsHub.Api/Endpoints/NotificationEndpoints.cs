using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/notifications — the signed-in account's own.
        //
        // Scoped to the session's own id and nothing else. There is no id
        // parameter to pass, so there is nothing to tamper with: an account
        // can only ever ask for its own.
        app.MapGet("/api/notifications", async (
            HttpContext http, SessionTokens tokens, AutoPartsContext db, CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            var mine = db.Notifications.Where(n => n.ClientId == session.UserId);

            var notifications = await mine
                .OrderByDescending(n => n.CreatedAt)
                .Take(50)
                .Select(n => new
                {
                    id = n.Id,
                    type = n.Type,
                    title = n.Title,
                    body = n.Body,
                    link = n.Link,
                    readAt = n.ReadAt,
                    createdAt = n.CreatedAt,
                })
                .AsNoTracking()
                .ToListAsync(ct);

            var unread = await mine.CountAsync(n => n.ReadAt == null, ct);

            return Results.Ok(new
            {
                unread,
                notifications = notifications.Select(n => new
                {
                    n.id, n.type, n.title, n.body, n.link,
                    readAt = Timestamps.Iso(n.readAt),
                    createdAt = Timestamps.Iso(n.createdAt)!,
                }),
            });
        });

        // POST /api/notifications — marks every unread one read.
        app.MapPost("/api/notifications", async (
            HttpContext http, SessionTokens tokens, AutoPartsContext db, CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            // Only the ones still unread, so a second call cannot rewrite when
            // the earlier ones were seen.
            var marked = await db.Database.ExecuteSqlAsync($"""
                UPDATE "Notification" SET "readAt" = SYSUTCDATETIME()
                WHERE "clientId" = {session.UserId} AND "readAt" IS NULL
                """, ct);

            return Results.Ok(new { ok = true, marked });
        });

        // PATCH /api/notifications/<id> — marks one read.
        //
        // The id comes from the caller, so ownership is checked by putting the
        // session's own id in the WHERE clause rather than by loading the row
        // and comparing: a row belonging to someone else simply matches
        // nothing, and there is no branch that can be forgotten.
        app.MapPatch("/api/notifications/{id}", async (
            string id, HttpContext http, SessionTokens tokens, AutoPartsContext db, CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            var changed = await db.Database.ExecuteSqlAsync($"""
                UPDATE "Notification" SET "readAt" = SYSUTCDATETIME()
                WHERE "id" = {id} AND "clientId" = {session.UserId} AND "readAt" IS NULL
                """, ct) > 0;

            var found = changed || await db.Notifications
                .AnyAsync(n => n.Id == id && n.ClientId == session.UserId, ct);

            // Not theirs or not real: the same 404 for both, so the endpoint
            // cannot be used to confirm that someone else's notification id
            // exists.
            if (!found) return Results.NotFound(new { error = "Notification not found." });

            // Already read — the caller's goal is met, so this is not a failure.
            return Results.Ok(new { ok = true, alreadyRead = !changed });
        });
    }
}

/// <summary>
/// Timestamps as the other API writes them.
/// </summary>
/// <remarks>
/// <c>Date.toISOString()</c> gives exactly three fractional digits and a Z,
/// always. .NET's round-trip format gives seven and an offset, so a response
/// would differ on every date it carried while being the same instant.
/// </remarks>
public static class Timestamps
{
    public static string? Iso(DateTime? value) => value is null
        ? null
        : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
