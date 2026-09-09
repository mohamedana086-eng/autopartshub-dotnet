namespace AutoPartsHub.Application.Abstractions;

/// <summary>
/// One transaction, held open across the several writes that have to land
/// together.
/// </summary>
/// <remarks>
/// The cases are named in the backlog and they are all the same shape: approve
/// an order and enqueue the hand-off to Odoo (T-180); retire, update and
/// insert a published price list (T-131); change a rule and bump the cache
/// version (T-108). Each is two writes that must not be observable apart.
///
/// WHY AN INTERFACE AND NOT THE DbContext
/// --------------------------------------
/// So that a use case can say "these go together" without naming Entity
/// Framework. That matters here more than usually: the import path is being
/// written against Dapper and SqlBulkCopy for speed, and the same transaction
/// has to cover both. A boundary owned by EF could not.
///
/// Not disposing without committing is a rollback, which is the behaviour a
/// <c>using</c> block should have — an exception on the way out must not leave
/// half of it written.
/// </remarks>
public interface IUnitOfWork
{
    Task<ITransaction> BeginAsync(CancellationToken ct = default);
}

/// <summary>Committed, or rolled back on the way out.</summary>
public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}
