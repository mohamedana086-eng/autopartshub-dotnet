namespace AutoPartsHub.Application.Abstractions;

/// <summary>A file that was stored, and what to ask for it back by.</summary>
/// <param name="Id">
/// Generated, not the name the uploader chose. What arrives is attacker-controlled
/// text: it can carry <c>../</c>, a null byte, a name that means something to
/// Windows (<c>CON</c>, <c>NUL</c>), or 4,000 characters. None of that can reach
/// a path if the path is not built from it.
/// </param>
/// <param name="OriginalName">Kept to show a person which file this was. Never used to build a path.</param>
public record StoredFile(string Id, string OriginalName, long Bytes);

/// <summary>
/// Where uploaded files go.
/// </summary>
/// <remarks>
/// Serves the manual import (T-124): a supplier's price list arrives as a
/// spreadsheet or an archive, is read, and is kept so that an import can be
/// explained afterwards.
///
/// Two rules the implementations owe, because they are what the task is
/// actually about rather than incidental:
///
/// <list type="number">
///   <item>
///     Nothing is stored under the web root. A file the caller chose the
///     contents of, served back from the application's own origin, is stored
///     cross-site scripting at best.
///   </item>
///   <item>
///     The path is built from <see cref="StoredFile.Id"/> and never from the
///     uploaded name.
///   </item>
/// </list>
///
/// Checking that a file is what it claims to be — extension against magic
/// bytes, and the size cap — belongs to the caller and not here: the rule is
/// about which uploads are acceptable, which is a decision about imports, and
/// a store that enforced it could not be used for anything else.
/// </remarks>
public interface IFileStore
{
    /// <summary>Stores the stream and gives back what to ask for it by.</summary>
    Task<StoredFile> SaveAsync(Stream content, string originalName, CancellationToken ct = default);

    /// <summary>Opens a stored file, or null if there is no such id.</summary>
    Task<Stream?> OpenAsync(string id, CancellationToken ct = default);

    /// <summary>Removes it. Removing something already gone is not an error.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
}
