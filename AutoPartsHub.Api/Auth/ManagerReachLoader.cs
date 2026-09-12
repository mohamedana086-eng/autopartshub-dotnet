using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Auth;

/// <summary>
/// Reads how far a salesperson's reach goes.
/// </summary>
/// <remarks>
/// The storage behind <see cref="ManagerReach"/>, which existed as three
/// degrees before there was anywhere to record the other two.
/// <see cref="Scope.WithReach"/> was left as the single seam for exactly this,
/// so landing the tables is a change to one resolver rather than to every
/// route.
///
/// ABSENT MEANS OWN
/// ----------------
/// No row is the same answer as a row saying <c>own</c>, and that is what
/// makes the migration safe to apply: every salesperson keeps the accounts
/// that name them and nothing else until somebody deliberately grants more.
/// A resolver that treated a missing row as an error would have made the
/// tables landing a change in behaviour for everybody.
///
/// A READ, AND ONLY FOR STAFF
/// --------------------------
/// It costs one query, and only on routes that have already decided the caller
/// is staff — <see cref="Scope.From"/> stays synchronous and database-free so
/// that a gate can be a singleton and an anonymous request never touches this.
/// </remarks>
public sealed class ManagerReachLoader(AutoPartsContext db)
{
    /// <summary>
    /// The same caller, with whatever reach has been recorded for them.
    /// </summary>
    /// <remarks>
    /// An admin is not narrowed by any of this and is not read for: their
    /// <see cref="Scope.ManagedBy"/> is already null, and a row granting an
    /// admin <c>selected</c> would be a narrowing rather than a grant.
    /// </remarks>
    public async Task<Scope> ResolveAsync(Scope scope, CancellationToken ct = default)
    {
        if (scope.UserId is null || scope.IsAdmin || !scope.IsStaff) return scope;

        var reach = await ReachFor(scope.UserId, ct);

        return reach == ManagerReach.Selected
            ? scope.WithReach(reach, await ExtraClientIdsFor(scope.UserId, ct))
            : scope.WithReach(reach);
    }

    /// <summary>What the table says, or <see cref="ManagerReach.Own"/>.</summary>
    /// <remarks>
    /// A value the CHECK constraint should have kept out still falls back to
    /// Own rather than throwing. The safe reading of a permission nobody
    /// recognises is the narrowest one, and a five-hundred on an admin screen
    /// because one row is odd helps nobody.
    /// </remarks>
    public async Task<ManagerReach> ReachFor(string managerId, CancellationToken ct = default)
    {
        var stored = await db.Database.SqlQuery<string>($"""
            SELECT "reach" AS "Value" FROM "ManagerAccess" WHERE "managerId" = {managerId}
            """).FirstOrDefaultAsync(ct);

        return Read(stored);
    }

    /// <summary>The stored word, as the enum. Anything unrecognised is Own.</summary>
    public static ManagerReach Read(string? reach) => reach switch
    {
        Selected => ManagerReach.Selected,
        All => ManagerReach.All,
        _ => ManagerReach.Own,
    };

    /// <summary>The enum, as the word the column holds.</summary>
    public static string Write(ManagerReach reach) => reach switch
    {
        ManagerReach.Selected => Selected,
        ManagerReach.All => All,
        _ => Own,
    };

    // The three values the CHECK constraint allows, in both schemas. Constants
    // rather than ToString().ToLower(), because the column and the enum are
    // two vocabularies that happen to agree and renaming one must not silently
    // rename the other.
    public const string Own = "own";
    public const string Selected = "selected";
    public const string All = "all";

    /// <summary>The accounts granted one at a time.</summary>
    /// <remarks>
    /// Ordered, so the same grant list reads the same way twice — it is
    /// serialised to an admin screen and compared between two APIs.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ExtraClientIdsFor(
        string managerId, CancellationToken ct = default) =>
        await db.Database.SqlQuery<string>($"""
            SELECT "clientId" AS "Value" FROM "ExtraClient"
            WHERE "managerId" = {managerId}
            ORDER BY "clientId" ASC
            """).ToListAsync(ct);
}
