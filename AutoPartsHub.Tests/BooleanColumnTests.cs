using System.Reflection;
using System.Text.RegularExpressions;

namespace AutoPartsHub.Tests;

/// <summary>
/// A truth value that leaves a statement has to leave it as a bit.
/// </summary>
/// <remarks>
/// PostgreSQL has a boolean type and SQL Server does not. <c>(a = b)</c> is a
/// VALUE there and a condition here, so the port turned every one of them into
/// <c>CASE WHEN a = b THEN 1 ELSE 0 END</c> — which is correct SQL and the
/// wrong TYPE. It produces an <c>int</c>, SqlClient hands that to
/// <c>GetBoolean</c>, and the read throws
/// <c>InvalidCastException: Unable to cast Int32 to Boolean</c>.
///
/// The fix is one word — <c>CAST(… AS bit)</c> — and the trouble is entirely
/// in noticing. The statement parses. It binds. Every name in it resolves. The
/// dialect check under NOEXEC passes it, because nothing is read until
/// something runs. So the failure waits on the exact request that reaches it.
///
/// THREE OF THEM SHIPPED
/// ---------------------
/// <c>PUT /api/admin/products/{id}/offers</c> answered 500 — the whole
/// supplier-offer editor — and the two client-detail reads did the same.
/// Found by a harness stumbling into one of them, not by reading. Nothing was
/// looking for the class, so this does.
///
/// WHAT IT CHECKS
/// --------------
/// Every <c>SqlQuery&lt;T&gt;</c> in the application, paired with the property
/// its alias lands on. An alias produced by a bare <c>CASE … THEN 1 ELSE 0
/// END</c> that maps to a <c>bool</c> is the bug; the same alias read back
/// into SQL — as VehicleFinder's nine filter flags are, compared to <c>1</c>
/// inside the statement they were made in — is not, and is not reported,
/// because no C# property carries it.
///
/// No database. This is text and reflection.
/// </remarks>
public partial class BooleanColumnTests
{
    /// <summary><c>SqlQuery&lt;RowType&gt;</c> and everything up to the next one.</summary>
    [GeneratedRegex(@"SqlQuery<(?<row>\w+)>")]
    private static partial Regex Query();

    /// <summary>An alias made by a CASE that yields 1 or 0, with no CAST around it.</summary>
    /// <remarks>
    /// The CAST is detected by looking at what comes BEFORE the CASE rather
    /// than by matching the whole expression, because the conditions inside
    /// one run to several lines and nest their own brackets.
    /// </remarks>
    [GeneratedRegex(
        @"(?<cast>CAST\s*\(\s*)?CASE\s+WHEN\b(?:(?!CASE\s+WHEN).)*?THEN\s+1\s+ELSE\s+0\s+END\s*(?:AS\s+bit\s*\))?\s*AS\s+""(?<alias>\w+)""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TruthValue();

    private static string[] SourceFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AutoPartsHub.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return Directory.GetFiles(
            Path.Combine(dir!.FullName, "AutoPartsHub.Api"), "*.cs", SearchOption.AllDirectories);
    }

    /// <summary>Every type the application can read a row into, by name.</summary>
    private static readonly Dictionary<string, Type> RowTypes =
        typeof(AutoPartsHub.Api.Auth.Scope).Assembly.GetTypes()
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First());

    private static bool IsBoolean(Type row, string alias) =>
        row.GetProperty(alias, BindingFlags.Public | BindingFlags.Instance) is { } property
        && (property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?));

    /// <remarks>
    /// A scan that matched nothing would make the assertion below pass by
    /// being vacuous — which, given this test exists because three of these
    /// shipped unnoticed, would be a particularly poor way to fail.
    /// </remarks>
    [Fact]
    public void FoundTheQueriesAndTheTruthValuesInThem()
    {
        var queries = 0;
        var truths = 0;

        foreach (var file in SourceFiles())
        {
            var source = File.ReadAllText(file);
            queries += Query().Matches(source).Count;
            truths += TruthValue().Matches(source).Count;
        }

        Assert.True(queries > 50, $"only found {queries} SqlQuery<> calls");
        Assert.True(truths > 8, $"only found {truths} CASE-as-truth-value columns");
    }

    /// <summary>
    /// A CASE landing on a bool is CAST to bit.
    /// </summary>
    /// <remarks>
    /// Attributed to the row type the statement names, so an alias only counts
    /// when something actually reads it as a bool. That is what keeps the nine
    /// filter flags in <c>VehicleFinder</c> out of it: they are compared to 1
    /// inside their own statement and never reach C#.
    /// </remarks>
    [Fact]
    public void EveryTruthValueReadAsABoolIsCastToBit()
    {
        var wrong = new List<string>();

        foreach (var file in SourceFiles())
        {
            var source = File.ReadAllText(file);
            var queries = Query().Matches(source);

            for (var i = 0; i < queries.Count; i++)
            {
                var name = queries[i].Groups["row"].Value;
                if (!RowTypes.TryGetValue(name, out var row)) continue;

                // Up to the next SqlQuery, which is where this statement ends
                // for the purpose of "which aliases does it produce".
                var start = queries[i].Index;
                var end = i + 1 < queries.Count ? queries[i + 1].Index : source.Length;

                foreach (Match found in TruthValue().Matches(source[start..end]))
                {
                    var alias = found.Groups["alias"].Value;
                    if (!IsBoolean(row, alias)) continue;
                    if (found.Groups["cast"].Success) continue;

                    wrong.Add($"{Path.GetFileName(file)}: {name}.{alias} "
                        + "is a bool and its CASE is not CAST to bit");
                }
            }
        }

        Assert.Empty(wrong);
    }
}
