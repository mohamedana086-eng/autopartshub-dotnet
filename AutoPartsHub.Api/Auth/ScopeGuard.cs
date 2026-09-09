using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Auth;

/// <summary>
/// Refusing an id that names something outside the caller's scope.
/// </summary>
/// <remarks>
/// WHEN TO USE THIS, AND WHEN NOT TO
/// ---------------------------------
/// Not for the id in the route. A write addressed at <c>/api/admin/orders/{id}</c>
/// carries its scope into the <c>UPDATE</c> itself —
/// <c>AND (@scope IS NULL OR c."salesManagerId" = @scope)</c> — and no row
/// matching means no row changed. That is strictly better than a check in
/// front of it: the check is one forgotten <c>return</c> away from being no
/// check at all, and between the check and the write somebody can move the
/// row. Every route in this application that scopes an addressed write does it
/// that way, and this class is not an invitation to stop.
///
/// It is for the id that arrives in the *body* and names something the
/// statement is not touching: "put this cart line against that offer",
/// "assign this customer to that manager", "approve this order and send it to
/// that supplier". There is no <c>WHERE</c> to hang those on, because the row
/// being written is not the row being named, so the only place to ask is
/// before the write.
///
/// WHY IT ANSWERS 403 AND THE ADDRESSED WRITES ANSWER 404
/// ------------------------------------------------------
/// The two are not inconsistent, though they look it. An addressed write that
/// matches nothing cannot tell "not yours" from "not there", and answering 404
/// for both is what stops a salesperson learning that another manager's order
/// exists by being told they may not touch it.
///
/// A body-borne id is a different situation: the caller already has a row they
/// are allowed to write, and is naming a second one to attach to it. Saying
/// "no" there does not disclose a list they could not otherwise enumerate, and
/// saying "not found" would send a client that is looking at a real, visible
/// offer into a retry loop. The backlog asks for 403 on every out-of-scope
/// mutation, and this is where that lives.
///
/// Both readings are defensible and the choice is one line —
/// <see cref="OutOfScope"/> — kept in one place precisely so that it can be
/// changed once rather than argued about per route.
/// </remarks>
public interface IScopeGuard
{
    /// <summary>The caller is an administrator.</summary>
    ScopeDecision EnsureAdmin(Scope scope);

    /// <summary>The caller is staff at all — an admin, or a manager.</summary>
    ScopeDecision EnsureStaff(Scope scope);

    /// <summary>That customer is one this caller looks after.</summary>
    Task<ScopeDecision> EnsureClientAsync(Scope scope, string clientId, CancellationToken ct = default);

    /// <summary>That supplier is one this caller speaks for.</summary>
    ScopeDecision EnsureSupplier(Scope scope, string supplierId);

    /// <summary>That order belongs to a customer this caller looks after.</summary>
    Task<ScopeDecision> EnsureOrderAsync(Scope scope, string orderId, CancellationToken ct = default);
}

/// <summary>Allowed, or the response to return instead.</summary>
/// <remarks>
/// Shaped like <see cref="GateResult"/> so that a handler reads the same way
/// whichever it called: <c>if (!d.Ok) return d.Response!;</c>. A decision that
/// carried a bare bool would leave each caller to word its own refusal, and
/// the wording is half of what is being decided.
/// </remarks>
public readonly record struct ScopeDecision(bool Ok, IResult? Response)
{
    public static readonly ScopeDecision Allowed = new(true, null);

    public static ScopeDecision Refused(IResult response) => new(false, response);
}

public sealed class ScopeGuard(AutoPartsContext db) : IScopeGuard
{
    public ScopeDecision EnsureAdmin(Scope scope) =>
        !scope.IsSignedIn ? NotSignedIn
        : scope.IsAdmin ? ScopeDecision.Allowed
        : Refuse("Admin access required.");

    public ScopeDecision EnsureStaff(Scope scope) =>
        !scope.IsSignedIn ? NotSignedIn
        : scope.IsStaff ? ScopeDecision.Allowed
        : Refuse("Admin access required.");

    public async Task<ScopeDecision> EnsureClientAsync(
        Scope scope, string clientId, CancellationToken ct = default)
    {
        if (!scope.IsSignedIn) return NotSignedIn;

        // Answered without a read wherever it can be: an admin, a manager with
        // the whole list, a customer who is not staff at all, or an account
        // already on this manager's granted list.
        if (scope.Reaches(clientId) is { } known)
        {
            return known ? ScopeDecision.Allowed : OutOfScope;
        }

        var managed = await db.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.SalesManagerId)
            .FirstOrDefaultAsync(ct);

        // A customer nobody manages, and one managed by somebody else, are the
        // same answer. So is one that does not exist — this is a body-borne id
        // and a distinct "no such customer" would make the endpoint an
        // enumeration oracle for ids that are not otherwise guessable.
        return managed is not null && managed == scope.UserId
            ? ScopeDecision.Allowed
            : OutOfScope;
    }

    public ScopeDecision EnsureSupplier(Scope scope, string supplierId) =>
        !scope.IsSignedIn ? NotSignedIn
        : scope.Speaks(supplierId) ? ScopeDecision.Allowed
        : OutOfScope;

    public async Task<ScopeDecision> EnsureOrderAsync(
        Scope scope, string orderId, CancellationToken ct = default)
    {
        if (!scope.IsSignedIn) return NotSignedIn;
        if (scope.IsAdmin) return ScopeDecision.Allowed;

        // One read rather than "whose order is this" followed by "do I reach
        // that customer": the second question is about a row the first already
        // has to load, and asking twice is a window in which the answer can
        // change.
        var owner = await db.Orders
            .Where(o => o.Id == orderId)
            .Select(o => new { o.ClientId, o.Client!.SalesManagerId })
            .FirstOrDefaultAsync(ct);

        if (owner is null) return OutOfScope;

        // A customer may act on their own order; staff on the orders of the
        // customers they reach.
        if (!scope.IsStaff) return owner.ClientId == scope.UserId ? ScopeDecision.Allowed : OutOfScope;

        return (scope.Reaches(owner.ClientId) ?? owner.SalesManagerId == scope.UserId)
            ? ScopeDecision.Allowed
            : OutOfScope;
    }

    private static ScopeDecision NotSignedIn =>
        ScopeDecision.Refused(Results.Json(new { error = "Not signed in." }, statusCode: 401));

    /// <summary>
    /// The one sentence, and the one status code, for "that is not yours".
    /// </summary>
    /// <remarks>
    /// Deliberately says nothing about what was named. The caller sent an id
    /// and learns only that they may not use it, which is all they are owed
    /// and all that can be said without confirming the row exists.
    /// </remarks>
    private static ScopeDecision OutOfScope => Refuse("That is not yours to change.");

    private static ScopeDecision Refuse(string error) =>
        ScopeDecision.Refused(Results.Json(new { error }, statusCode: 403));
}
