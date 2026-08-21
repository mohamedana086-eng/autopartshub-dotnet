namespace AutoPartsHub.Api.Auth;

/// <summary>
/// Gate for every /api/admin route.
/// </summary>
/// <remarks>
/// The admin pages are protected by a guard in the storefront, which does
/// nothing for the API — without this, any signed-in customer, or an anonymous
/// caller, could read the client list or rewrite markup rules by hitting the
/// endpoints directly. Call it first in every admin handler.
/// </remarks>
public sealed class AdminGate(SessionTokens tokens)
{
    /// <summary>The session, or the response to return instead.</summary>
    public GateResult RequireAdmin(HttpContext http)
    {
        var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);

        if (session is null) return GateResult.Refused(NotSignedIn);
        if (session.Role != Roles.Admin) return GateResult.Refused(NotAdmin);

        return GateResult.Allowed(session, isAdmin: true);
    }

    /// <summary>
    /// Gate for the handful of admin reads a SALES account may also make.
    /// </summary>
    /// <remarks>
    /// Deliberately a second method rather than a relaxation of the first:
    /// every route keeps whatever it already had, and letting SALES somewhere
    /// new has to be a visible edit to that route. Opting in is the only way
    /// in.
    ///
    /// It hands back the session because scoping is the caller's job — knowing
    /// the request is from staff is not enough, the query has to be narrowed
    /// to that salesperson's own customers. <see cref="GateResult.ScopeTo"/>
    /// is what says whether to narrow.
    /// </remarks>
    public GateResult RequireStaff(HttpContext http)
    {
        var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);

        if (session is null) return GateResult.Refused(NotSignedIn);
        if (session.Role is not (Roles.Admin or Roles.Sales)) return GateResult.Refused(NotAdmin);

        return GateResult.Allowed(session, isAdmin: session.Role == Roles.Admin);
    }

    private static IResult NotSignedIn => Results.Json(new { error = "Not signed in." }, statusCode: 401);
    private static IResult NotAdmin => Results.Json(new { error = "Admin access required." }, statusCode: 403);
}

public record GateResult(bool Ok, SessionPayload? Session, bool IsAdmin, IResult? Response)
{
    public static GateResult Allowed(SessionPayload session, bool isAdmin) => new(true, session, isAdmin, null);
    public static GateResult Refused(IResult response) => new(false, null, false, response);

    /// <summary>
    /// The manager id to narrow a query to, or null for the whole table.
    /// </summary>
    /// <remarks>
    /// Reading it as one value rather than as a flag plus an id is what keeps
    /// the queries honest: a scoped query takes this and a null means admin,
    /// so there is no branch where a narrowing can be left out.
    /// </remarks>
    public string? ScopeTo => IsAdmin ? null : Session!.UserId;
}
