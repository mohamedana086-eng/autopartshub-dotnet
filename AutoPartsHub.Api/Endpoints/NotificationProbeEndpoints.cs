using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Removes a notification by id.
/// </summary>
/// <remarks>
/// DEVELOPMENT ONLY — see the guard at the call site in Program.cs. Neither
/// API offers a way to unsend one, and that is correct: a notification is
/// something a person was told, and an admin quietly deleting the evidence is
/// not a feature.
///
/// It exists so the comparison script can send a real notification through
/// each API and not leave two invented messages sitting in a live account's
/// list. It deletes by id, so it can only remove a row whose id the caller
/// already has — which in practice means one the same script just created.
/// </remarks>
public static class NotificationProbeEndpoints
{
    public static void MapNotificationProbe(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/forget-notification", async (
            ForgetNotificationRequest body, AutoPartsContext db, CancellationToken ct) =>
        {
            var removed = await db.Database.ExecuteSqlAsync(
                $"""DELETE FROM "Notification" WHERE "id" = {body.NotificationId}""", ct);

            return Results.Ok(new { removed });
        });
    }
}

public record ForgetNotificationRequest(string NotificationId);
