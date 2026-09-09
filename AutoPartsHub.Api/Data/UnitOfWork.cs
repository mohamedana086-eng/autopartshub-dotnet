using AutoPartsHub.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsHub.Api.Data;

/// <summary>
/// <see cref="IUnitOfWork"/> over the request's own <see cref="AutoPartsContext"/>.
/// </summary>
/// <remarks>
/// It lives here rather than in Infrastructure because the DbContext does, and
/// moving the DbContext is T-013. When it moves, this comes with it and
/// nothing that holds the interface changes — which is the entire argument for
/// the interface existing before the move rather than after it.
///
/// Entity Framework's transaction is shared with anything using the same
/// connection, so a Dapper command or a SqlBulkCopy enlisted on
/// <c>db.Database.GetDbConnection()</c> is inside it too. That is what the
/// import path needs: the bulk load and the three set-based statements that
/// publish it have to land together, and neither half is written in EF.
/// </remarks>
public sealed class UnitOfWork(AutoPartsContext db) : IUnitOfWork
{
    public async Task<ITransaction> BeginAsync(CancellationToken ct = default) =>
        new EfTransaction(await db.Database.BeginTransactionAsync(ct));

    private sealed class EfTransaction(IDbContextTransaction transaction) : ITransaction
    {
        public Task CommitAsync(CancellationToken ct = default) => transaction.CommitAsync(ct);

        // Disposing without committing rolls back — Entity Framework's own
        // behaviour, and the reason a `using` block is the right shape for
        // this: an exception on the way out must not leave half of it written.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
