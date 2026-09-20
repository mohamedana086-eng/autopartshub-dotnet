using System.Text.RegularExpressions;

namespace AutoPartsHub.Tests;

/// <summary>
/// Which admin writes a SALES account has been let into.
/// </summary>
/// <remarks>
/// A SALES account used to be a scoped VIEWER: reads narrowed to its own
/// customers, and not one write. Two writes have since been opened, and
/// opening a write is not the same kind of change as opening a read — a read
/// that forgets to narrow shows somebody a row they should not see, and a
/// write that forgets to narrow changes it.
///
/// So the list is asserted rather than described. Putting
/// <c>RequireOperator</c> on an endpoint fails this test until the endpoint is
/// named below, which makes letting SALES somewhere new a decision somebody
/// wrote down rather than a line that went past in a diff. The other API has
/// the same test over its own routes.
///
/// It reads the endpoint sources as text because what is being asserted is
/// which gate the source names — a fact about the text, not about a running
/// route table.
/// </remarks>
public partial class AdminWriteScopeTests
{
    /// <summary>
    /// Every admin write a salesperson may make.
    /// </summary>
    /// <remarks>
    /// Nothing else is on this list, and two things are deliberately absent.
    /// <c>PATCH /api/admin/clients/{id}</c> writes the role, the pricing tier,
    /// the discount, the currency and who owns the account — every one of them
    /// a thing a salesperson must not set. And everything under pricing sets
    /// what the whole catalogue costs, which is not a per-customer decision at
    /// all, so there is nothing there for a scope to narrow.
    /// </remarks>
    private static readonly string[] OpenToSales =
    [
        // Telling a customer their part is in is the same job as looking
        // after them.
        "/api/admin/notifications",
        // Moving their own customers' orders along.
        "/api/admin/orders/{id}",
        // The same permission in the shape the backlog specifies: the status
        // in the path instead of the body. Nothing new is open here — a
        // salesperson who can PATCH an order to `accepted` is the one pressing
        // approve — and they are listed rather than folded into the line above
        // because this list is read as "which writes", not "which handlers".
        "/api/admin/orders/{id}/approve",
        "/api/admin/orders/{id}/reject",
        "/api/admin/orders/{id}/cancel",
        // Correcting a tracking number after the fact. It moves nothing and
        // touches two columns, on an order that is already theirs to move.
        "/api/admin/orders/{id}/shipping",
        // Answering their own customers' tickets, and resolving them. A
        // support queue an admin has to type into on somebody else's behalf is
        // a queue that does not get answered.
        "/api/admin/tickets/{id}",
        "/api/admin/tickets/{id}/messages",
    ];

    private const string Operator = "RequireOperator";

    [GeneratedRegex(@"app\.Map(Get|Post|Patch|Put|Delete)\(\s*""(?<path>[^""]+)""")]
    private static partial Regex Mapping();

    /// <summary>The endpoint sources, found by walking up to the solution.</summary>
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

    /// <summary>One mapped endpoint, and the source between it and the next.</summary>
    private record Endpoint(string Method, string Path, string Body);

    private static List<Endpoint> Endpoints()
    {
        var found = new List<Endpoint>();

        foreach (var file in EndpointFiles())
        {
            var source = File.ReadAllText(file);
            var matches = Mapping().Matches(source);

            for (var i = 0; i < matches.Count; i++)
            {
                var start = matches[i].Index;
                // Up to the next mapping, which is where this handler ends for
                // the purpose of "which gate did it call".
                var end = i + 1 < matches.Count ? matches[i + 1].Index : source.Length;

                found.Add(new Endpoint(
                    matches[i].Groups[1].Value,
                    matches[i].Groups["path"].Value,
                    source[start..end]));
            }
        }

        return found;
    }

    private static readonly HashSet<string> WriteMethods = ["Post", "Patch", "Put", "Delete"];

    [Fact]
    public void FoundAPlausibleNumberOfEndpoints()
    {
        // A scan that matched nothing would make every assertion below pass by
        // being vacuous, which is the way this kind of test fails silently.
        var admin = Endpoints().Where(e => e.Path.StartsWith("/api/admin/")).ToList();

        Assert.True(admin.Count > 20, $"only found {admin.Count} admin endpoints");
        Assert.True(admin.Count(e => WriteMethods.Contains(e.Method)) > 8);
    }

    [Fact]
    public void EveryAdminEndpointIsGated()
    {
        foreach (var endpoint in Endpoints().Where(e => e.Path.StartsWith("/api/admin/")))
        {
            var gated = endpoint.Body.Contains("RequireAdmin(")
                || endpoint.Body.Contains("RequireStaff(")
                || endpoint.Body.Contains($"{Operator}(");

            Assert.True(gated, $"{endpoint.Method} {endpoint.Path} calls no guard");
        }
    }

    [Fact]
    public void TheWritesOpenToSalesAreExactlyTheListThatWasDecidedOn()
    {
        var opened = Endpoints()
            .Where(e => WriteMethods.Contains(e.Method) && e.Body.Contains($"{Operator}("))
            .Select(e => e.Path)
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(OpenToSales.Order().ToArray(), opened);
    }

    [Fact]
    public void NoWriteSneaksInThroughTheReadGateInstead()
    {
        // RequireStaff is for reads. A write reaching for it would be the same
        // permission granted without the name that says a write was granted —
        // and would slip past the list above.
        var sneaking = Endpoints()
            .Where(e => WriteMethods.Contains(e.Method)
                     && e.Body.Contains("RequireStaff(")
                     && !e.Body.Contains($"{Operator}("))
            .Select(e => $"{e.Method} {e.Path}")
            .ToArray();

        Assert.Empty(sneaking);
    }

    [Fact]
    public void TheCustomerRecordStaysAdminOnly()
    {
        // Named on its own rather than left to the list, because this is the
        // endpoint that most looks like it belongs with the other two and must
        // not be: it writes the role, the tier, the discount, the currency and
        // who owns the account.
        var clients = Endpoints().Single(e =>
            e.Method == "Patch" && e.Path == "/api/admin/clients/{id}");

        Assert.Contains("RequireAdmin(", clients.Body);
        Assert.DoesNotContain($"{Operator}(", clients.Body);
    }

    [Fact]
    public void EverythingThatSetsPricesStaysAdminOnly()
    {
        var pricing = Endpoints()
            .Where(e => WriteMethods.Contains(e.Method))
            .Where(e => e.Path.StartsWith("/api/admin/markup-rules")
                     || e.Path.StartsWith("/api/admin/price-lists")
                     || e.Path.StartsWith("/api/admin/currencies")
                     || e.Path.StartsWith("/api/admin/client-categories")
                     || e.Path.StartsWith("/api/admin/goods-categories"))
            .ToList();

        Assert.True(pricing.Count > 3, $"only found {pricing.Count} pricing writes");

        foreach (var endpoint in pricing)
        {
            Assert.DoesNotContain($"{Operator}(", endpoint.Body);
            Assert.Contains("RequireAdmin(", endpoint.Body);
        }
    }

    [Fact]
    public void AnOpenedWriteNarrowsWhatItTouches()
    {
        // Not proof that the SQL is right — that is what the statements
        // themselves and the differential harness are for. It is proof that
        // the endpoint asked the question, which is the step that gets
        // forgotten: a RequireOperator with no ScopeTo beside it is a write
        // open to every salesperson for every customer.
        foreach (var endpoint in Endpoints().Where(e => e.Body.Contains($"{Operator}(")))
        {
            Assert.Contains("ScopeTo", endpoint.Body);
        }
    }
}
