using System.Text.RegularExpressions;

namespace AutoPartsHub.Tests;

/// <summary>
/// Which writes answer somebody who is not signed in.
/// </summary>
/// <remarks>
/// <see cref="AdminRouteGuard"/> holds everything under <c>/api/admin</c>, and
/// <see cref="AdminWriteScopeTests"/> asserts that every route there names a
/// gate. Neither says anything at all about the rest of the application, and
/// the rest of the application is where the customer's writes live — their
/// basket, their orders, their tickets.
///
/// That gap was found by adding <c>PATCH /api/tickets/{id}/status</c>: a write
/// that changes a ticket, outside <c>/api/admin</c>, which the path-based
/// middleware does not reach and the source-level test did not look at. It was
/// gated, but nothing would have noticed if it had not been.
///
/// So this is the other half. A write outside <c>/api/admin</c> either reads
/// the session or is named below as deliberately public, and adding one that
/// is neither fails here — which makes "this endpoint needs no sign-in" a
/// sentence somebody wrote rather than a line that went past in a diff.
/// </remarks>
public partial class PublicWriteTests
{
    /// <summary>
    /// The writes that answer an anonymous caller, and why each may.
    /// </summary>
    /// <remarks>
    /// Every one of them is a route somebody uses BEFORE they have an account
    /// or a session, which is the only reason that survives asking.
    /// </remarks>
    private static readonly Dictionary<string, string> Public = new()
    {
        // Signing in cannot require being signed in.
        ["POST /api/auth/login"] = "the sign-in itself",
        ["POST /api/auth/register"] = "there is no account yet",
        ["POST /api/auth/register-supplier"] = "a supplier applying, before they are one",
        ["POST /api/suppliers/register"] = "the older path to the same thing",

        // Account recovery reaches somebody who cannot sign in by definition
        // — that is what they are recovering from. Each is held instead by a
        // one-time token that went to their address; see VerificationTokens.
        ["POST /api/auth/password/forgot"] = "they cannot sign in; that is the problem",
        ["POST /api/auth/password/reset"] = "held by the token in the link, not a session",
        ["POST /api/auth/email/confirm"] = "the link is opened from an inbox, often another browser",

        // A POST because the list is too long for a query string, not because
        // it changes anything. It reads the catalogue and prices it for
        // whoever is asking, anonymous included — the same as search.
        ["POST /api/catalog/bulk"] = "a read that needs a body",
    };

    [GeneratedRegex(@"app\.Map(Get|Post|Patch|Put|Delete)\(\s*""(?<path>[^""]+)""")]
    private static partial Regex Mapping();

    private static readonly HashSet<string> WriteMethods = ["Post", "Patch", "Put", "Delete"];

    /// <summary>Reads the session, names an admin gate, or is a supplier's own.</summary>
    private static bool Gated(string body) =>
        body.Contains("SessionTokens.CookieName")
        || body.Contains("RequireAdmin(")
        || body.Contains("RequireStaff(")
        || body.Contains("RequireOperator(")
        || body.Contains("RequireSupplier(");

    private static string[] EndpointFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AutoPartsHub.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return Directory.GetFiles(
            Path.Combine(dir!.FullName, "AutoPartsHub.Api", "Endpoints"), "*.cs");
    }

    private record Endpoint(string Method, string Path, string Body);

    private static List<Endpoint> Writes()
    {
        var found = new List<Endpoint>();

        foreach (var file in EndpointFiles())
        {
            var source = File.ReadAllText(file);
            var matches = Mapping().Matches(source);

            for (var i = 0; i < matches.Count; i++)
            {
                var method = matches[i].Groups[1].Value;
                var path = matches[i].Groups["path"].Value;
                if (!WriteMethods.Contains(method)) continue;

                // Up to the next mapping, which is where this handler ends for
                // the purpose of "what did it name".
                var start = matches[i].Index;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : source.Length;

                found.Add(new Endpoint(method.ToUpperInvariant(), path, source[start..end]));
            }
        }

        return found;
    }

    /// <remarks>
    /// A scan that matched nothing would make the assertion below pass by
    /// being vacuous, which is how this kind of test fails silently.
    /// </remarks>
    [Fact]
    public void FoundAPlausibleNumberOfWrites()
    {
        var writes = Writes().Where(e => e.Path.StartsWith("/api/")).ToList();

        Assert.True(writes.Count > 20, $"only found {writes.Count} writes");
        Assert.Contains(writes, e => e.Path == "/api/cart");
    }

    /// <summary>
    /// Every write outside <c>/api/admin</c> is gated, or named as public.
    /// </summary>
    /// <remarks>
    /// The <c>/dev</c> probes are excluded by the <c>/api/</c> prefix rather
    /// than by name: they are mapped only under
    /// <c>app.Environment.IsDevelopment()</c> and return 404 in a deployment,
    /// so they are not a route a customer can reach at all.
    /// </remarks>
    [Fact]
    public void NoWriteOutsideAdminAnswersAStrangerUnlessItSaysSo()
    {
        var open = Writes()
            .Where(e => e.Path.StartsWith("/api/") && !e.Path.StartsWith("/api/admin/"))
            .Where(e => !Gated(e.Body))
            .Select(e => $"{e.Method} {e.Path}")
            .Where(name => !Public.ContainsKey(name))
            .Order()
            .ToArray();

        Assert.Empty(open);
    }

    /// <summary>
    /// And the list does not outlive what is on it.
    /// </summary>
    /// <remarks>
    /// A name left behind after its route was gated, renamed or removed is an
    /// exemption sitting there for the next route that happens to match it.
    /// </remarks>
    [Fact]
    public void EveryNamedPublicWriteIsStillOneAndStillPublic()
    {
        var writes = Writes().ToDictionary(e => $"{e.Method} {e.Path}", e => e.Body);

        foreach (var (name, why) in Public)
        {
            Assert.True(writes.ContainsKey(name), $"{name} is named public and no longer exists");
            Assert.False(Gated(writes[name]), $"{name} is gated now and can leave the list ({why})");
        }
    }
}
