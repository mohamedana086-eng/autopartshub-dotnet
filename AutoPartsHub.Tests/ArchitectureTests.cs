using System.Reflection;
using AutoPartsHub.Domain.Pricing;

namespace AutoPartsHub.Tests;

/// <summary>
/// The layering, asserted rather than described.
/// </summary>
/// <remarks>
/// The pricing rules, the order lifecycle and the part-number vocabulary have
/// no database and no web framework in them, and the point of putting them in
/// their own project is that this stops being a claim about discipline and
/// starts being a thing the compiler enforces. A layering that lives only in a
/// README is a layering that lasts until the first afternoon somebody needs a
/// <c>DbContext</c> in a hurry.
///
/// The direction is what matters. The API may reach down into the domain; the
/// domain may not reach up. The moment it can, "where does this rule live" has
/// no answer, and the rules stop being testable without a database — which is
/// what makes the six hundred tests in this project run in under a second.
///
/// Two of these already caught something. <c>OrderStatuses</c> and
/// <c>OrderFilters</c> were reaching up into the admin validators for
/// <c>Validated&lt;T&gt;</c>, and <c>OrderFilters.Read</c> took ASP.NET's own
/// <c>IQueryCollection</c> — a rule about which dates are valid, pinned to the
/// framework that happened to deliver them.
/// </remarks>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(PricingEngine).Assembly;

    /// <summary>
    /// Assemblies the domain must never reach for.
    /// </summary>
    /// <remarks>
    /// Matched on a prefix, so a new package from any of these families is
    /// caught without this list being updated — which is the only way a list
    /// like this survives contact with a dependency graph.
    /// </remarks>
    private static readonly string[] Forbidden =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.Hosting",
        "Npgsql",
        "Dapper",
        "StackExchange.Redis",
        "Hangfire",
        "BCrypt",
    ];

    [Fact]
    public void TheDomainReferencesNothingItShouldNot()
    {
        var reached = Domain.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(name => Forbidden.Any(f => name.StartsWith(f, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            reached.Count == 0,
            $"AutoPartsHub.Domain has picked up: {string.Join(", ", reached)}. "
            + "The rules in it are meant to be testable without a database or a web server.");
    }

    /// <remarks>
    /// The assembly check above passes trivially if the domain is empty or if
    /// the reference went missing, so the population is asserted too — the way
    /// this kind of test fails silently is by measuring nothing.
    /// </remarks>
    [Fact]
    public void TheDomainIsNotEmpty()
    {
        var types = Domain.GetExportedTypes();

        Assert.True(types.Length > 20, $"only {types.Length} public types in the domain");
        Assert.Contains(types, t => t.Name == "PricingEngine");
        Assert.Contains(types, t => t.Name == "OrderStatuses");
    }

    /// <remarks>
    /// Belt and braces over the assembly check: a type can be reached through a
    /// transitive reference the assembly table does not list directly, and a
    /// signature is where that would show up first.
    /// </remarks>
    [Fact]
    public void NoDomainTypeSpeaksInFrameworkTypes()
    {
        var offenders = new List<string>();

        foreach (var type in Domain.GetExportedTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                                   | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var mentioned = method.GetParameters().Select(p => p.ParameterType)
                    .Append(method.ReturnType)
                    .Select(t => t.Assembly.GetName().Name ?? "")
                    .Where(name => Forbidden.Any(f => name.StartsWith(f, StringComparison.Ordinal)));

                foreach (var name in mentioned)
                {
                    offenders.Add($"{type.Name}.{method.Name} -> {name}");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    // ------------------------------------------------------ the source side

    private static DirectoryInfo SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AutoPartsHub.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static string[] DomainSources() => Directory.GetFiles(
        Path.Combine(SolutionRoot().FullName, "AutoPartsHub.Domain"), "*.cs", SearchOption.AllDirectories);

    [Fact]
    public void TheDomainNeverReachesUpIntoTheApi()
    {
        var offenders = DomainSources()
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("using AutoPartsHub.Api"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"these reach up into the API: {string.Join(", ", offenders)}");
    }

    /// <remarks>
    /// A project file with a package in it is the easiest way to undo all of
    /// this, and the one least likely to be noticed in a diff: adding a
    /// <c>PackageReference</c> is one line and pulls a whole framework behind
    /// it. The domain is allowed exactly what the shared framework gives it.
    /// </remarks>
    [Fact]
    public void TheDomainProjectTakesNoPackages()
    {
        var project = File.ReadAllText(Path.Combine(
            SolutionRoot().FullName, "AutoPartsHub.Domain", "AutoPartsHub.Domain.csproj"));

        Assert.DoesNotContain("PackageReference", project);
        Assert.DoesNotContain("ProjectReference", project);
    }
}
