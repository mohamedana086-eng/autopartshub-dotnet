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

    /// <summary>
    /// Gate for the handful of admin WRITES a SALES account may also make.
    /// </summary>
    /// <remarks>
    /// A third method that checks exactly what <see cref="RequireStaff"/>
    /// checks, and that is the point. A salesperson who may read a list is not
    /// thereby somebody who may change it, and one gate covering both would
    /// make every <c>RequireStaff</c> on a GET look like permission to add a
    /// POST beside it. Two names mean the question "which writes has SALES
    /// been let into" is answerable by grepping for one of them.
    ///
    /// WHAT THIS DOES NOT DO
    /// ---------------------
    /// It does not scope anything. Knowing the request is from staff is not
    /// enough for a write and is *less* enough than for a read: a read that
    /// forgets to narrow shows somebody a row they should not see, and a write
    /// that forgets to narrow changes it. Every caller passes
    /// <see cref="GateResult.ScopeTo"/> into the statement itself, so the
    /// narrowing is part of the UPDATE rather than a check standing in front
    /// of it — a check in front is one forgotten return away from being no
    /// check at all, and it races anything that moves the row in between.
    ///
    /// A write that finds nothing to change answers 404, not 403. To a
    /// salesperson, another manager's order does not exist; saying "you may
    /// not touch that one" would confirm that it does.
    /// </remarks>
    public GateResult RequireOperator(HttpContext http) => RequireStaff(http);

    private static IResult NotSignedIn => Results.Json(new { error = "Not signed in." }, statusCode: 401);
    private static IResult NotAdmin => Results.Json(new { error = "Admin access required." }, statusCode: 403);
}

public record GateResult(bool Ok, SessionPayload? Session, bool IsAdmin, IResult? Response, Scope Scope)
{
    public static GateResult Allowed(SessionPayload session, bool isAdmin) =>
        new(true, session, isAdmin, null, Scope.From(session));

    public static GateResult Refused(IResult response) =>
        new(false, null, false, response, Scope.Anonymous);

    /// <summary>
    /// The manager id to narrow a query to, or null for the whole table.
    /// </summary>
    /// <remarks>
    /// Reading it as one value rather than as a flag plus an id is what keeps
    /// the queries honest: a scoped query takes this and a null means admin,
    /// so there is no branch where a narrowing can be left out.
    ///
    /// A view onto <see cref="Auth.Scope.ManagedBy"/> rather than a second
    /// calculation of the same thing. It stays because seventy statements bind
    /// it by this name, and because "the value that goes in the WHERE" is what
    /// a query wants to be handed; what changed is that there is now one place
    /// deciding it, which is where the manager reach degrees will arrive.
    /// </remarks>
    public string? ScopeTo => Scope.ManagedBy;
}
