namespace AutoPartsHub.Api.Auth;

/// <summary>
/// The signing key, or a refusal to start signing with a known one.
/// </summary>
/// <remarks>
/// Development keeps the placeholder, because a local app that will not run
/// until you invent a secret is a worse first five minutes for no gain. In
/// production the three ways to end up forgeable — unset, still the example
/// value, or something too short to matter — all stop here instead.
///
/// Pure and static, so the rules can be tested without a process to set
/// environment variables on.
/// </remarks>
public static class AuthSecret
{
    /// <summary>
    /// The value the example env file ships with, and what development falls
    /// back to. It is published — the example file, the README and the git
    /// history all carry it — so it is a secret in name only. Anyone who has
    /// seen the repository can mint an ADMIN cookie for any deployment still
    /// signing with it.
    /// </summary>
    private const string DevPlaceholder = "dev-only-insecure-secret-change-me";

    /// <summary>
    /// <c>openssl rand -hex 32</c>, the command the README gives, produces 64
    /// characters. The floor is set well under that so a different but sound
    /// generator is not refused, and well over anything worth guessing at.
    /// </summary>
    private const int MinimumLength = 32;

    public static string Resolve(string? configured, bool isProduction)
    {
        if (!isProduction) return string.IsNullOrEmpty(configured) ? DevPlaceholder : configured;

        if (string.IsNullOrEmpty(configured))
        {
            throw new InvalidOperationException(
                "AUTH_SECRET is not set. Sessions would be signed with a published " +
                "placeholder, so anyone could forge an admin cookie. Generate one " +
                "with `openssl rand -hex 32`.");
        }
        if (configured == DevPlaceholder)
        {
            throw new InvalidOperationException(
                "AUTH_SECRET is still the value from the example file, which is published " +
                "and lets anyone forge an admin cookie. Generate one with " +
                "`openssl rand -hex 32`.");
        }
        if (configured.Length < MinimumLength)
        {
            throw new InvalidOperationException(
                $"AUTH_SECRET is {configured.Length} characters; {MinimumLength} is " +
                "the minimum. Generate one with `openssl rand -hex 32`.");
        }

        return configured;
    }
}

/// <summary>
/// The roles a Client row can carry.
/// </summary>
/// <remarks>
/// A plain string column in the database, so the set lives here rather than
/// being generated. Anything unrecognised narrows to the least-privileged
/// role, which is what makes a typo in the column safe rather than a way in.
/// </remarks>
public static class Roles
{
    public const string Admin = "ADMIN";
    public const string Sales = "SALES";
    /// <summary>An account that speaks for a supplier — see Client.supplierId.</summary>
    public const string SupplierRole = "SUPPLIER";
    public const string B2B = "B2B";
    public const string Retail = "RETAIL";

    public static string Narrow(string? value) =>
        value is Admin or Sales or SupplierRole or B2B ? value : Retail;

    /// <summary>
    /// Staff see the admin panel; what they see inside is scoped.
    /// </summary>
    /// <remarks>
    /// SUPPLIER is deliberately not staff. Selling through this shop is not
    /// the same as working in it, and the admin panel carries every customer's
    /// orders and every supplier's costs.
    /// </remarks>
    public static bool IsStaff(string? role) => Narrow(role) is Admin or Sales;

    public static bool IsAdmin(string? role) => Narrow(role) == Admin;
}
