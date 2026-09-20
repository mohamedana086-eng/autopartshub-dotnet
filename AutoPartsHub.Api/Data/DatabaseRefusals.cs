using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoPartsHub.Api.Data;

/// <summary>Why the database refused a write.</summary>
public enum DatabaseRefusal
{
    /// <summary>Not a refusal this application words for a person.</summary>
    None,

    /// <summary>A value that has to be unique already exists.</summary>
    Unique,

    /// <summary>A CHECK constraint said no.</summary>
    Check,

    /// <summary>A row referred to something that is not there.</summary>
    ForeignKey,
}

/// <summary>
/// What the database said, in words neither engine uses.
/// </summary>
/// <remarks>
/// Three places in this application catch a failed write and do something
/// other than let it become a 500: a reference collision is retried with a new
/// reference, and a CHECK violation on the shelves becomes a sentence telling
/// an admin to run the reconciliation. All three were written against
/// <c>PostgresException</c> and its five-character SQLSTATE.
///
/// Against SQL Server those catches do not fire at all. The exception is a
/// <c>SqlException</c>, it carries a number rather than a SQLSTATE, and
/// nothing about the shape is shared — so the order that collided on its
/// reference would surface as a stack trace instead of being retried, and the
/// admin would get one instead of the sentence.
///
/// That is the failure mode worth naming: not a crash on the first request,
/// but the handful of paths that only run when something has already gone
/// wrong, quietly ceasing to work. They are also the paths least likely to be
/// exercised by hand before a cutover.
///
/// WHY A CLASSIFIER AND NOT A BASE CLASS
/// -------------------------------------
/// The two exceptions have no useful common ancestor — <c>DbException</c>
/// carries nothing that tells a unique violation from a deadlock. So the
/// knowledge of which number means what lives here, once, and the call sites
/// ask a question in their own vocabulary.
///
/// The SQL Server numbers are asserted against a real engine in
/// <c>DatabaseRefusalTests</c> by provoking each violation, rather than taken
/// from documentation. 547 in particular covers both CHECK and FOREIGN KEY,
/// and only the message says which.
/// </remarks>
public static class DatabaseRefusals
{
    // PostgreSQL SQLSTATE, class 23 — integrity constraint violation.
    private const string PgUnique = "23505";
    private const string PgCheck = "23514";
    private const string PgForeignKey = "23503";

    // SQL Server error numbers. 2627 is a unique CONSTRAINT and 2601 a unique
    // INDEX; which one a table gets depends on how the uniqueness was declared,
    // and this schema has both.
    private const int SqlUniqueConstraint = 2627;
    private const int SqlUniqueIndex = 2601;

    /// <summary>
    /// CHECK and FOREIGN KEY share a number; the message names which.
    /// </summary>
    private const int SqlConstraintConflict = 547;

    /// <summary>What the database refused, or <see cref="DatabaseRefusal.None"/>.</summary>
    /// <remarks>
    /// Unwraps <see cref="DbUpdateException"/>: EF wraps whatever the provider
    /// threw, and a caller that catches on the outside would see nothing
    /// useful. The raw SQL paths throw the provider exception directly, so
    /// both shapes arrive here.
    /// </remarks>
    public static DatabaseRefusal Of(Exception? error) => error switch
    {
        null => DatabaseRefusal.None,

        DbUpdateException wrapped => Of(wrapped.InnerException),

        PostgresException pg => pg.SqlState switch
        {
            PgUnique => DatabaseRefusal.Unique,
            PgCheck => DatabaseRefusal.Check,
            PgForeignKey => DatabaseRefusal.ForeignKey,
            _ => DatabaseRefusal.None,
        },

        SqlException sql => sql.Number switch
        {
            SqlUniqueConstraint or SqlUniqueIndex => DatabaseRefusal.Unique,
            // The message is the only thing that tells the two apart, and it
            // is worth being exact about: reading a foreign-key failure as a
            // CHECK would tell an admin to run a stock reconciliation for a
            // row that referred to a supplier who no longer exists.
            SqlConstraintConflict when sql.Message.Contains("CHECK constraint", StringComparison.Ordinal)
                => DatabaseRefusal.Check,
            SqlConstraintConflict => DatabaseRefusal.ForeignKey,
            _ => DatabaseRefusal.None,
        },

        _ => DatabaseRefusal.None,
    };

    /// <summary>Whether this is a refusal of the given kind.</summary>
    public static bool Is(this Exception error, DatabaseRefusal refusal) => Of(error) == refusal;
}
