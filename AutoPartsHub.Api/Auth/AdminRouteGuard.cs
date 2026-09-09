namespace AutoPartsHub.Api.Auth;

/// <summary>
/// Nothing under <c>/api/admin</c> is reachable by somebody who is not staff.
/// </summary>
/// <remarks>
/// Every admin route already calls a gate, and a test asserts that every one
/// of them does. So why a second lock in front of all of them.
///
/// Because the test asserts a fact about the source text — that the handler
/// names <c>RequireAdmin</c> or <c>RequireOperator</c> — and the thing that
/// actually refuses the request is the <c>if (!g.Ok) return</c> after it. A
/// handler can name the gate and go on to answer anyway: an early return added
/// above it, a refactor that drops the guard clause while keeping the call, a
/// route mapped somewhere the test does not walk. Every one of those passes the
/// source check and serves the client list.
///
/// This is the lock that does not depend on a handler remembering. It is
/// deliberately the weakest useful one — staff, not admin — because admin and
/// manager is a distinction the handlers make and make differently: a manager
/// may move their own customers' orders and may not touch a markup rule. What
/// this stops is a customer, a supplier or a stranger reaching an admin route
/// at all, which is the failure that matters and the one a forgotten gate
/// causes.
///
/// It answers exactly what <see cref="AdminGate"/> answers, in the same words,
/// so a route reaching it produces no new response shape for the storefront to
/// learn.
///
/// PATH MATCHING, NOT ROUTE MATCHING
/// ---------------------------------
/// It runs on the path rather than on the matched endpoint, so it does not
/// need routing to have run and cannot be skipped by a route that was never
/// mapped. <c>StartsWithSegments</c> and not <c>StartsWith</c>: the second
/// would match <c>/api/administrators</c>, and — worse the other way round —
/// a comparison that missed a trailing segment boundary is how a guard ends up
/// not covering the route it was written for.
/// </remarks>
public sealed class AdminRouteGuard(RequestDelegate next, SessionTokens tokens)
{
    private static readonly PathString Admin = new("/api/admin");

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(Admin))
        {
            await next(context);
            return;
        }

        var session = tokens.Decode(context.Request.Cookies[SessionTokens.CookieName]);

        if (session is null)
        {
            await Refuse(context, StatusCodes.Status401Unauthorized, "Not signed in.");
            return;
        }

        if (!Roles.IsStaff(session.Role))
        {
            await Refuse(context, StatusCodes.Status403Forbidden, "Admin access required.");
            return;
        }

        await next(context);
    }

    private static Task Refuse(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error });
    }
}
