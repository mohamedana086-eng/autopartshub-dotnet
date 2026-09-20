using AutoPartsHub.Api.Auth;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// What the signed-in member of staff may see.
/// </summary>
/// <remarks>
/// The screens narrow themselves by this. A salesperson with their own
/// accounts should not be shown a customer filter listing the whole company,
/// and one given every customer should not be shown a filter that pretends
/// otherwise — both are the same page rendering what the caller can act on.
///
/// It answers about the CALLER and nobody else. "How far does this other
/// person's reach go" is an admin question about somebody's permissions, and
/// it belongs on the screen that grants them rather than here; asking it
/// through a route that answers for whoever is signed in is how a route that
/// returns your own settings turns into one that returns anybody's.
/// </remarks>
public static class ManagerScopeEndpoints
{
    public static void MapManagerScopeEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/admin/manager/scope
        app.MapGet("/api/admin/manager/scope", async (
            HttpContext http, AdminGate gate, ManagerReachLoader reaches, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            var scope = await reaches.ResolveAsync(g.Scope, ct);

            return Results.Ok(new
            {
                userId = scope.UserId,
                role = scope.Role,
                // The word the column holds, not the enum's name. It is the
                // same vocabulary the admin screen writes back.
                reach = ManagerReachLoader.Write(scope.Reach),
                // Empty for every reach but Selected — see Scope.WithReach for
                // why a list granted last month does not come back to life.
                extraClientIds = scope.ExtraClientIds,
                // The two questions a screen actually asks, answered here
                // rather than re-derived from the three fields above. A UI
                // working out "does this person see everybody" from a role and
                // a reach is a fourth place that rule lives.
                seesEveryCustomer = scope.ManagedBy is null,
                isAdmin = scope.IsAdmin,
            });
        });
    }
}
