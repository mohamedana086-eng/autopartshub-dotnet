using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Endpoints;

namespace AutoPartsHub.Api.Catalogue;

/// <summary>
/// The one place a supplier's published identity is decided.
/// </summary>
/// <remarks>
/// A customer must not learn who the shop buys from. Knowing that a brake disc
/// comes from a named distributor is knowing where to buy it a step cheaper,
/// and the whole margin the platform earns sits in that gap. So a supplier has
/// two names: the real one, and <c>Supplier.Code</c> — a short opaque handle —
/// and which one goes out depends on who is asking.
///
/// WHY THE CODE GOES IN THE `name` FIELD
/// -------------------------------------
/// Rather than adding a second field, or omitting the name for customers, the
/// code is published *as* the name. The response shape is then the same for
/// everybody, and the storefront renders a supplier the same way whoever is
/// signed in. The alternative — a nullable name plus a code, decided in the
/// template — is one forgotten `??` away from showing the real one, and that
/// mistake is invisible in review because both fields are populated.
///
/// WHY ONE TYPE AND NOT A HELPER PER ROUTE
/// ---------------------------------------
/// Six routes carry a supplier reference. A rule applied six times is a rule
/// broken the seventh time somebody adds a route, so the naming decision is
/// made once, here, and the routes ask for it rather than implementing it.
/// <c>SupplierAnonymityTests</c> is what keeps that true: it walks all six as
/// every non-staff role and fails on a real name in a response body.
///
/// SUPPLIERS ARE NOT STAFF
/// -----------------------
/// <see cref="Roles.IsStaff"/> is ADMIN and SALES only, so a signed-in
/// supplier sees codes for everyone including, incidentally, themselves. That
/// is the right way round: a supplier reading the catalogue is reading their
/// competitors' offers, and the portal is where they see their own name.
/// </remarks>
public readonly record struct SupplierNaming(bool ShowsRealNames)
{
    public static SupplierNaming For(string? role) => new(Roles.IsStaff(role));

    /// <summary>Resolved from the session cookie, for routes that do not price.</summary>
    /// <remarks>
    /// The priced routes already know the role — it is on
    /// <see cref="Pricing.RequestPricing.ClientRole"/>, read from the account
    /// rather than the cookie — and pass it to the overload above. The two
    /// directory routes price nothing and would otherwise load an account for
    /// no other reason, so they read the cookie. The cookie is signed, and a
    /// role that went stale between sign-ins can only ever be *narrower* than
    /// the account's if somebody was promoted, never wider: a demoted admin's
    /// cookie is the case that matters and it is handled by
    /// <see cref="AdminGate"/> refusing them everywhere it counts.
    /// </remarks>
    public static SupplierNaming For(HttpContext http, SessionTokens tokens) =>
        For(tokens.Decode(http.Request.Cookies[SessionTokens.CookieName])?.Role);

    /// <summary>The name to publish for a supplier with this name and code.</summary>
    public string Of(string name, string code) => ShowsRealNames ? name : code;

    /// <summary>
    /// As above, for a row that may carry no supplier at all.
    /// </summary>
    /// <remarks>
    /// A separate name rather than an overload: C# does not tell two methods
    /// apart by nullability, and the interesting half is the null handling.
    /// Staff fall back to the code when a row somehow has one and no name;
    /// nobody else ever falls back to the name, because a fallback that can
    /// reach the real name is a leak waiting for the row that triggers it.
    /// <c>Supplier.Code</c> is not nullable, so a customer looking at a real
    /// supplier always has something to be shown.
    /// </remarks>
    public string? OfMaybe(string? name, string? code) => ShowsRealNames ? name ?? code : code;

    /// <summary>
    /// The supplier on a search result or a part's page, or null where the
    /// part has no supplier.
    /// </summary>
    /// <remarks>
    /// Takes the whole row's worth rather than a name, so that a caller cannot
    /// build the DTO with the naming applied to one field and forgotten on the
    /// next one somebody adds.
    /// </remarks>
    public SearchSupplierDto? Search(
        string? slug, string? name, string? code,
        int? rating, string? reliability, bool? acceptsReturns) =>
        slug is null ? null : new SearchSupplierDto(
            slug, OfMaybe(name, code) ?? slug, rating, reliability ?? "standard", acceptsReturns);
}

/// <summary>A supplier's two names, before one of them has been chosen.</summary>
public record SupplierNames(string Name, string Code);
