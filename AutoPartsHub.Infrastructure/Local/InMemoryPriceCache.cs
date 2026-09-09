using System.Collections.Concurrent;
using AutoPartsHub.Application.Abstractions;

namespace AutoPartsHub.Infrastructure.Local;

/// <summary>
/// The price cache, in this process, until there is a Redis.
/// </summary>
/// <remarks>
/// Behaves the way the Redis one will have to (T-107): keys are scoped to a
/// version, a bump moves every one of them out of reach at once, and nothing
/// is deleted when it does. Writing it this way now is what makes the swap a
/// class in this folder rather than a change to how invalidation works.
///
/// The one behaviour it cannot have is sharing. Two API processes each keep
/// their own, so a rule changed on one is still cached on the other until its
/// entries expire. That is why this is the development adapter and not the
/// production one, and it is the reason the version is read through an
/// interface rather than held in a field somewhere — in Redis the version is
/// shared, and everything else about the shape is already right.
/// </remarks>
public sealed class InMemoryPriceCache : IPriceCache
{
    private readonly ConcurrentDictionary<string, (decimal Price, DateTimeOffset Until)> _entries = new();
    private readonly TimeProvider _time;
    private long _version;

    public InMemoryPriceCache(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public Task<long> VersionAsync(CancellationToken ct = default) =>
        Task.FromResult(Interlocked.Read(ref _version));

    public Task BumpVersionAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _version);
        return Task.CompletedTask;
    }

    public Task<decimal?> GetAsync(string key, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(key, out var entry)) return Task.FromResult<decimal?>(null);

        // Expiry is checked on read rather than swept. Nothing here runs on a
        // timer, and an entry nobody asks for costs a dictionary slot.
        if (entry.Until <= _time.GetUtcNow())
        {
            _entries.TryRemove(key, out _);
            return Task.FromResult<decimal?>(null);
        }

        return Task.FromResult<decimal?>(entry.Price);
    }

    public Task SetAsync(string key, decimal price, TimeSpan ttl, CancellationToken ct = default)
    {
        _entries[key] = (price, _time.GetUtcNow() + ttl);
        return Task.CompletedTask;
    }

    /// <summary>How many entries are held. For the tests and for a health page.</summary>
    public int Count => _entries.Count;
}
