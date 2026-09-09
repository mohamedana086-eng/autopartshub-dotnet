using AutoPartsHub.Application.Abstractions;

namespace AutoPartsHub.Infrastructure.Local;

/// <summary>
/// A handful of articles, so that the code around the catalogue provider can
/// be written and run before there is a subscription.
/// </summary>
/// <remarks>
/// BLK-001. Deliberately tiny and deliberately fixed: it exists so that a
/// lookup path has something to return, not so that anything can be developed
/// against its contents. Matching is case-insensitive and ignores the
/// separators people type into part numbers, because that is the one piece of
/// behaviour the callers genuinely have to handle.
/// </remarks>
public sealed class FakeTecDocClient : ITecDocClient
{
    private static readonly TecDocArticle[] Articles =
    [
        new("0986424815", "BOSCH", "Brake pad set, disc brake", ["1605998", "93188424"]),
        new("0451103316", "BOSCH", "Oil filter", ["650311", "93156552"]),
        new("W71230", "MANN-FILTER", "Oil filter", ["650311"]),
    ];

    private static string Normalise(string raw) =>
        new(raw.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public Task<TecDocArticle?> FindArticleAsync(
        string manufacturer, string articleNumber, CancellationToken ct = default)
    {
        var wanted = Normalise(articleNumber);
        var brand = Normalise(manufacturer);

        return Task.FromResult(Articles.FirstOrDefault(a =>
            Normalise(a.ArticleNumber) == wanted && Normalise(a.Manufacturer) == brand));
    }

    public Task<IReadOnlyList<TecDocArticle>> FindByOeNumberAsync(
        string oeNumber, CancellationToken ct = default)
    {
        var wanted = Normalise(oeNumber);

        IReadOnlyList<TecDocArticle> found =
            [.. Articles.Where(a => a.OeNumbers.Any(oe => Normalise(oe) == wanted))];

        return Task.FromResult(found);
    }
}
