namespace AutoPartsHub.Api.Auth;

/// <summary>
/// How much of the customer list a manager has been given.
/// </summary>
/// <remarks>
/// <see cref="Own"/> is the only degree this schema can currently produce:
/// <c>Client.salesManagerId</c> says who looks after an account, and there is
/// nowhere yet to record that a manager was given somebody else's as well, or
/// all of them. The other two are declared anyway, because every query and
/// every guard has to handle them and adding a case to an enum later means
/// revisiting each one — which is the rework this package exists to prevent.
///
/// The tables that make the other two reachable are <c>ManagerAccess</c> and
/// <c>ExtraClient</c>, and they belong to the Prisma schema in the storefront
/// repository rather than here. Until they land,
/// <see cref="Scope.From(SessionPayload)"/> produces <see cref="Own"/> and
/// nothing else, and the tests below pin down what the other two will mean
/// when it can.
/// </remarks>
public enum ManagerReach
{
    /// <summary>The accounts that name this manager, and no others.</summary>
    Own,

    /// <summary>Their own, plus a list somebody granted them one at a time.</summary>
    Selected,

    /// <summary>Every customer. Still not every route — see <see cref="Scope.IsAdmin"/>.</summary>
    All,
}

/// <summary>
/// Who is asking, and what they are allowed to be asking about.
/// </summary>
/// <remarks>
/// One immutable value per request, read once, passed down. The point of
/// gathering it into a type is that "what may this caller touch" stops being a
/// question each handler answers for itself out of a session payload and a
/// role string — five roles times three manager degrees is fifteen answers,
/// and fifteen answers spread across seventy routes is where an account ends
/// up reading somebody else's orders.
///
/// HOW IT IS USED
/// --------------
/// Mostly as a value bound into a statement, not as a check standing in front
/// of one. <see cref="ManagedBy"/> goes into the <c>WHERE</c> of the read and
/// the <c>UPDATE</c> alike, so the narrowing is part of the write rather than
/// a gate before it: a check in front is one forgotten <c>return</c> away from
/// being no check at all, and it races anything that moves the row in between.
/// <see cref="IScopeGuard"/> covers the cases a <c>WHERE</c> cannot reach —
/// an id that arrives in a request body and names something the statement is
/// not touching.
///
/// A ROLE THAT IS NOT STAFF
/// ------------------------
/// <see cref="ManagedBy"/> hands back the caller's own id for a customer, who
/// manages nobody, so a customer query narrowed by it matches nothing. That is
/// the right way for this to fail: the routes are gated to staff before they
/// get here, and if one ever is not, it returns an empty list rather than the
/// table.
/// </remarks>
/// <param name="UserId">Null for an anonymous caller.</param>
/// <param name="Role">Already narrowed — see <see cref="Roles.Narrow"/>.</param>
/// <param name="ExtraClientIds">Granted one at a time, under <see cref="ManagerReach.Selected"/>.</param>
/// <param name="SupplierIds">The suppliers this caller speaks for. One, or none.</param>
public sealed record Scope(
    string? UserId,
    string Role,
    ManagerReach Reach,
    IReadOnlyList<string> ExtraClientIds,
    IReadOnlyList<string> SupplierIds)
{
    /// <summary>Nobody signed in: a retail visitor with no id and no reach.</summary>
    public static readonly Scope Anonymous =
        new(null, Roles.Retail, ManagerReach.Own, [], []);

    /// <summary>
    /// The scope a session alone can establish.
    /// </summary>
    /// <remarks>
    /// Deliberately synchronous and database-free, so that
    /// <see cref="AdminGate"/> can stay a singleton and every admin route can
    /// build one without a round trip. The parts that need a read — a
    /// supplier's own id, and one day a manager's granted list — are added by
    /// the gate that already does that read, through <see cref="ForSupplier"/>
    /// and <see cref="WithReach"/>.
    /// </remarks>
    public static Scope From(SessionPayload? session) =>
        session is null
            ? Anonymous
            : new(session.UserId, Roles.Narrow(session.Role), ManagerReach.Own, [], []);

    /// <summary>The same caller, speaking for one supplier.</summary>
    public Scope ForSupplier(string supplierId) => this with { SupplierIds = [supplierId] };

    /// <summary>
    /// The same caller, with the reach an admin granted them.
    /// </summary>
    /// <remarks>
    /// Nothing calls this yet — see <see cref="ManagerReach"/> for why. It is
    /// the single seam the storage plugs into when it exists, so that landing
    /// the tables is a change to one resolver rather than to every route.
    /// </remarks>
    public Scope WithReach(ManagerReach reach, IReadOnlyList<string>? extraClientIds = null) =>
        this with
        {
            Reach = reach,
            // Extras only mean anything under Selected. Dropping them on the
            // way out of that mode is what stops a list granted last month
            // coming back to life when somebody is moved to Selected again.
            ExtraClientIds = reach == ManagerReach.Selected ? extraClientIds ?? [] : [],
        };

    public bool IsSignedIn => UserId is not null;

    public bool IsAdmin => Roles.IsAdmin(Role);

    public bool IsStaff => Roles.IsStaff(Role);

    public bool IsSupplier => Role == Roles.SupplierRole;

    /// <summary>
    /// The manager id a customer query narrows to, or null for the whole table.
    /// </summary>
    /// <remarks>
    /// Read as one value rather than as a flag plus an id, which is what keeps
    /// the queries honest: a scoped statement takes this and a null means no
    /// narrowing, so there is no branch where the narrowing can be left out.
    ///
    /// Null for an admin, and for a manager given <see cref="ManagerReach.All"/>
    /// — the two are the same question to a <c>WHERE</c> clause and a different
    /// question to a policy route, which is what <see cref="IsAdmin"/> is for.
    /// </remarks>
    public string? ManagedBy => IsAdmin || (IsStaff && Reach == ManagerReach.All) ? null : UserId;

    /// <summary>
    /// Whether this caller reaches that customer, as far as can be told
    /// without asking the database who manages them.
    /// </summary>
    /// <remarks>
    /// Three-valued on purpose. True and false are answers; null means the
    /// question needs <c>Client.salesManagerId</c>, which is
    /// <see cref="IScopeGuard.EnsureClientAsync"/>'s job. A two-valued version
    /// would have to guess, and the safe guess — false — would refuse a
    /// manager their own customers.
    /// </remarks>
    public bool? Reaches(string clientId)
    {
        if (IsAdmin) return true;
        if (!IsStaff) return false;
        if (Reach == ManagerReach.All) return true;
        if (ExtraClientIds.Contains(clientId)) return true;

        // Own, or Selected and not on the granted list: it comes down to who
        // the account names as its manager.
        return null;
    }

    /// <summary>Whether this caller speaks for that supplier.</summary>
    public bool Speaks(string supplierId) => IsAdmin || SupplierIds.Contains(supplierId);
}
