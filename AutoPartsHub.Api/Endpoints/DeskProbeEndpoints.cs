using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Takes back the two things the admin desk can create but not remove.
/// </summary>
/// <remarks>
/// DEVELOPMENT ONLY — see the guard at the call site in Program.cs. Neither
/// API offers a way to unsend a notification or delete an account, and both
/// omissions are correct: a notification is something a person was told, and
/// an account is somebody's history of orders.
///
/// These exist so the comparison scripts can send a real notification and open
/// a real account through each API and leave neither behind. Both delete by
/// id, so they can only remove a row whose id the caller already has — which
/// in practice means one the same script just created.
/// </remarks>
public static class DeskProbeEndpoints
{
    public static void MapDeskProbes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/forget-notification", async (
            ForgetNotificationRequest body, AutoPartsContext db, CancellationToken ct) =>
        {
            var removed = await db.Database.ExecuteSqlAsync(
                $"""DELETE FROM "Notification" WHERE "id" = {body.NotificationId}""", ct);

            return Results.Ok(new { removed });
        });

        app.MapPost("/dev/forget-client", async (
            ForgetClientRequest body, AutoPartsContext db, CancellationToken ct) =>
        {
            // Refuses an account with any order history rather than cascading
            // through it. A registration test's account has none; anything
            // that does is not the test's to delete, and finding that out from
            // a count is better than finding out from what is missing.
            var orders = await db.Orders.CountAsync(o => o.ClientId == body.ClientId, ct);
            if (orders > 0) return Results.Conflict(new { error = "That account has orders.", orders });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var carts = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM "CartItem" ci USING "Cart" c
                WHERE c."id" = ci."cartId" AND c."clientId" = {body.ClientId}
                """, ct);
            await db.Database.ExecuteSqlAsync(
                $"""DELETE FROM "Cart" WHERE "clientId" = {body.ClientId}""", ct);
            await db.Database.ExecuteSqlAsync(
                $"""DELETE FROM "Notification" WHERE "clientId" = {body.ClientId}""", ct);
            var removed = await db.Database.ExecuteSqlAsync(
                $"""DELETE FROM "Client" WHERE "id" = {body.ClientId}""", ct);

            await transaction.CommitAsync(ct);

            return Results.Ok(new { removed, carts });
        });
    }
}

public record ForgetNotificationRequest(string NotificationId);

public record ForgetClientRequest(string ClientId);
