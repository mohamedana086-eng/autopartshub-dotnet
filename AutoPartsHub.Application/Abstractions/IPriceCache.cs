namespace AutoPartsHub.Application.Abstractions;

/// <summary>
/// Remembering what a part priced to, so the chain does not run twice.
/// </summary>
/// <remarks>
/// Serves T-107 and T-108. The pricing chain walks every active rule for every
/// row of every search, and the rules change a few times a day while the
/// searches happen a few times a second.
///
/// INVALIDATION IS A VERSION, NOT A SWEEP
/// --------------------------------------
/// Every key is prefixed with <see cref="VersionAsync"/>, and changing a rule,
/// a band, a category or publishing a price list bumps it. Nothing is deleted:
/// the old keys stop being asked for and expire on their own. That is the
/// whole reason it is a version and not a scan — finding every key a rule
/// change invalidated means walking the keyspace, which is the operation
/// nobody may run against a live Redis.
///
/// The bump belongs in the same transaction as the change that caused it, so
/// there is no window where the rule is saved and the old price is still being
/// served.
///
/// WHAT MUST NOT BE CACHED
/// -----------------------
/// A price that depended on a rule naming one client. The key cannot describe
/// it without becoming per-customer, and a per-customer key caches nothing.
/// The caller decides and skips the cache; an implementation cannot tell.
///
/// Every method may fail quietly. A cache that is down is a slow shop, and a
/// cache that is down and throwing is a closed one.
/// </remarks>
public interface IPriceCache
{
    /// <summary>The current version. Every key is scoped to it.</summary>
    Task<long> VersionAsync(CancellationToken ct = default);

    /// <summary>Moves every key out of reach at once. Deletes nothing.</summary>
    Task BumpVersionAsync(CancellationToken ct = default);

    /// <summary>The cached price, or null when it is not there or cannot be read.</summary>
    Task<decimal?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Remembers a price. Failing to is not an error the caller can act on.</summary>
    Task SetAsync(string key, decimal price, TimeSpan ttl, CancellationToken ct = default);
}
