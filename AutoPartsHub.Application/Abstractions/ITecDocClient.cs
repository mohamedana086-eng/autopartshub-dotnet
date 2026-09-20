namespace AutoPartsHub.Application.Abstractions;

/// <summary>One article as the catalogue data provider describes it.</summary>
public record TecDocArticle(
    string ArticleNumber,
    string Manufacturer,
    string Name,
    IReadOnlyList<string> OeNumbers);

/// <summary>
/// The catalogue data the shop does not own.
/// </summary>
/// <remarks>
/// Stands in for BLK-001. There is no subscription yet, and the gap report
/// excludes it from the score for that reason — what is not excluded is being
/// ready for it, which means the lookups the search already needs are named
/// here rather than assumed to arrive in some shape later.
///
/// Deliberately read-only and deliberately small. Everything the platform
/// knows about its own stock, prices and suppliers is its own; this is only
/// the part that is licensed.
/// </remarks>
public interface ITecDocClient
{
    /// <summary>The article with that number from that brand, or null.</summary>
    Task<TecDocArticle?> FindArticleAsync(
        string manufacturer, string articleNumber, CancellationToken ct = default);

    /// <summary>Everything that answers to an OE number — what a cross-reference is.</summary>
    Task<IReadOnlyList<TecDocArticle>> FindByOeNumberAsync(
        string oeNumber, CancellationToken ct = default);
}
