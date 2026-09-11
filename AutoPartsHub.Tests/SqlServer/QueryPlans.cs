using System.Data;
using System.Data.Common;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// How SQL Server says it will answer a statement this application sent.
/// </summary>
/// <remarks>
/// T-067's acceptance is not about what the near-miss search returns — it is
/// about how it gets there: "no leading wildcard, no wide scan". That is a
/// claim about the execution plan, and the only thing that can settle it is
/// the optimizer.
///
/// Asserting it on the SQL TEXT would be the easy version and would not be
/// worth much: <c>LIKE 'ABC%'</c> against a column with no index on it is a
/// scan, and reads exactly like the version that seeks. The two are told apart
/// by the plan and by nothing else.
///
/// So the statement is captured as the application sends it — command text and
/// parameters both, through an interceptor — and handed back to the server
/// under <c>SHOWPLAN_XML</c>, which compiles it and returns the plan without
/// running it. A test that re-typed the SQL would drift from the code it was
/// meant to be about; this one cannot.
/// </remarks>
public sealed class PlanCapture : DbCommandInterceptor
{
    private readonly List<Captured> captured = [];

    /// <summary>Statements seen since the last <see cref="Clear"/>.</summary>
    public IReadOnlyList<Captured> Statements => captured;

    public void Clear() => captured.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Remember(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Remember(command);
        return ValueTask.FromResult(result);
    }

    /// <remarks>
    /// Copied rather than held: the command and its parameters belong to EF
    /// and are gone by the time a test looks at them.
    /// </remarks>
    private void Remember(DbCommand command) =>
        captured.Add(new Captured(
            command.CommandText,
            [.. command.Parameters.Cast<DbParameter>()
                .Select(p => new Bound(At(p.ParameterName), p.Value, p.DbType, p.Size))]));

    /// <summary>
    /// The name as the SQL spells it.
    /// </summary>
    /// <remarks>
    /// EF names its parameters "p0" and writes "@p0" in the statement; the
    /// sigil is added by the driver on the way out. Anything rebuilding the
    /// batch by hand has to add it back, and the symptom of not doing so is a
    /// syntax error rather than a wrong plan, which is at least the good kind.
    /// </remarks>
    private static string At(string name) =>
        name.StartsWith('@') ? name : "@" + name;

    public readonly record struct Bound(string Name, object? Value, DbType Type, int Size);

    public sealed record Captured(string Sql, Bound[] Parameters)
    {
        /// <summary>The plan SQL Server compiles for this statement.</summary>
        /// <remarks>
        /// Its own connection, because SHOWPLAN_XML changes what every
        /// statement on a connection does — turned on for the one being asked
        /// about and nowhere near the one under test.
        ///
        /// WHY THE STATEMENT IS RE-SENT AS TEXT
        /// ------------------------------------
        /// A command carrying parameters is sent by SqlClient as an RPC, and
        /// under SHOWPLAN_XML an RPC comes back with no result set at all —
        /// not an empty plan, no columns. Wrapping it in <c>sp_executesql</c>
        /// is no better: the plan returned for that is one line saying EXECUTE
        /// PROC, with the statement inside left uncompiled.
        ///
        /// What does compile is the statement itself, with its parameters
        /// declared as local variables ahead of it. That is not quite the
        /// query the application sends, and the difference is worth being
        /// precise about: the optimizer cannot see the VALUE in a variable, so
        /// it falls back to a fixed guess at how many rows will match instead
        /// of reading the histogram for the actual one.
        ///
        /// That makes this test PESSIMISTIC, which is the safe direction. The
        /// guess is a large fraction of the table, so it argues for a scan; a
        /// plan that seeks anyway will seek all the more readily once the real
        /// value is known. A seek here cannot be an accident of a lucky
        /// parameter, and the failure it can produce is the false alarm, never
        /// the false all-clear.
        /// </remarks>
        public async Task<QueryPlan> PlanAsync()
        {
            await using var connection = new SqlConnection(SqlServer.ConnectionString);
            await connection.OpenAsync();

            await using (var on = connection.CreateCommand())
            {
                on.CommandText = "SET SHOWPLAN_XML ON";
                await on.ExecuteNonQueryAsync();
            }

            await using var asked = connection.CreateCommand();
            asked.CommandText = AsBatch();

            var xml = new System.Text.StringBuilder();
            await using (var reader = await asked.ExecuteReaderAsync())
            {
                do
                {
                    while (await reader.ReadAsync()) xml.Append(reader.GetString(0));
                }
                while (await reader.NextResultAsync());
            }

            if (xml.Length == 0)
            {
                throw new Xunit.Sdk.XunitException("SQL Server returned no plan for: " + Sql);
            }

            return new QueryPlan(Sql, XDocument.Parse(xml.ToString()));
        }

        /// <summary>This statement and its parameters, as one batch of text.</summary>
        private string AsBatch() =>
            Parameters.Length == 0
                ? Sql
                : string.Join(Environment.NewLine, Parameters
                      .Select(p => $"DECLARE {Declaration(p)} = {Literal(p)};")
                      .Append(Sql));

        private static string Quoted(string value) => "N'" + value.Replace("'", "''") + "'";

        /// <remarks>
        /// Only the types these statements actually use. Anything else throws
        /// rather than being guessed at: a type declared wrong here produces a
        /// plan for a query the application does not send, which would read as
        /// a passing test.
        /// </remarks>
        private static string Declaration(Bound parameter) =>
            parameter.Name + " " + parameter.Type switch
            {
                DbType.Int32 => "int",
                DbType.Int64 => "bigint",
                DbType.Boolean => "bit",
                DbType.Double => "float",
                DbType.String or DbType.StringFixedLength =>
                    parameter.Size is > 0 and <= 4000 ? $"nvarchar({parameter.Size})" : "nvarchar(4000)",
                DbType.AnsiString or DbType.AnsiStringFixedLength =>
                    parameter.Size is > 0 and <= 8000 ? $"varchar({parameter.Size})" : "varchar(8000)",
                _ => throw new Xunit.Sdk.XunitException(
                    $"No T-SQL type known for {parameter.Type} ({parameter.Name}). "
                    + "Add it here rather than letting the plan be for a different query."),
            };

        private static string Literal(Bound parameter) => parameter.Value switch
        {
            null or DBNull => "NULL",
            string text => Quoted(text),
            bool yes => yes ? "1" : "0",
            IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            var other => throw new Xunit.Sdk.XunitException(
                $"No literal known for {other.GetType().Name} ({parameter.Name})."),
        };
    }
}

/// <summary>One compiled plan, asked the two questions T-067 cares about.</summary>
public sealed class QueryPlan(string sql, XDocument plan)
{
    private static readonly XNamespace Showplan =
        "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public string Sql { get; } = sql;

    /// <summary>Every physical operator, e.g. "Index Seek", "Table Scan".</summary>
    public IReadOnlyList<string> Operations =>
        [.. plan.Descendants(Showplan + "RelOp")
            .Select(op => (string?)op.Attribute("PhysicalOp") ?? "")];

    /// <summary>Every table or index the plan reads, as "Table.Index".</summary>
    public IReadOnlyList<string> Reads =>
        [.. plan.Descendants(Showplan + "Object")
            .Select(o => Unbracket((string?)o.Attribute("Table"))
                         + "." + Unbracket((string?)o.Attribute("Index")))];

    /// <summary>
    /// The operators that read more of a table than they were asked for.
    /// </summary>
    /// <remarks>
    /// A Clustered Index Scan is a table scan wearing the name of an index,
    /// which is why it counts here rather than being trusted for containing
    /// the word "Index".
    ///
    /// Only operators that actually name a table, though. "Constant Scan"
    /// reads nothing — it is how the optimizer expresses a row it made up —
    /// and counting it would make every plan look guilty of the one thing
    /// this is trying to find.
    /// </remarks>
    public IReadOnlyList<string> Scans =>
        [.. plan.Descendants(Showplan + "RelOp")
            .Where(op => ((string?)op.Attribute("PhysicalOp") ?? "")
                    .EndsWith(" Scan", StringComparison.Ordinal)
                && op.Descendants(Showplan + "Object").Any())
            .Select(op => (string?)op.Attribute("PhysicalOp") + " of "
                + Unbracket((string?)op.Descendants(Showplan + "Object")
                    .First().Attribute("Table")))];

    private static string Unbracket(string? value) => value?.Trim('[', ']') ?? "";

    public override string ToString() => $"{Sql}\n\n{string.Join(", ", Operations)}";
}
