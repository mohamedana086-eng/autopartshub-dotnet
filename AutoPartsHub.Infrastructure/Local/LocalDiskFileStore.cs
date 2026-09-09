using AutoPartsHub.Application.Abstractions;

namespace AutoPartsHub.Infrastructure.Local;

/// <summary>
/// Uploaded files on this machine's disk, outside anything the web server serves.
/// </summary>
/// <remarks>
/// The development answer to <see cref="IFileStore"/>, and a serviceable
/// single-server production one. Replacing it with S3 or a share is a class in
/// this folder.
///
/// The name the uploader chose never reaches the path. It is kept beside the
/// file, in a sidecar, so that an import log can say "this came from
/// prices-august.xlsx" without anything having to trust that string — and the
/// path itself is the generated id, which is hex, so there is nothing in it to
/// escape.
/// </remarks>
public sealed class LocalDiskFileStore : IFileStore
{
    private readonly string _root;

    /// <param name="root">
    /// Where files go. Must be outside the web root; the default is a sibling
    /// of the content root rather than a folder under it, so that a
    /// misconfigured static-file middleware cannot start serving uploads.
    /// </param>
    public LocalDiskFileStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFile> SaveAsync(
        Stream content, string originalName, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("n");

        var path = PathFor(id);
        long bytes;
        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, ct);
            bytes = file.Length;
        }

        // The original name beside the file rather than in it. Reading it back
        // is for showing a person; nothing builds a path from it.
        await File.WriteAllTextAsync(SidecarFor(id), originalName, ct);

        return new StoredFile(id, originalName, bytes);
    }

    public Task<Stream?> OpenAsync(string id, CancellationToken ct = default)
    {
        if (!IsWellFormed(id)) return Task.FromResult<Stream?>(null);

        var path = PathFor(id);
        return Task.FromResult<Stream?>(
            File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!IsWellFormed(id)) return Task.CompletedTask;

        // Delete on a missing file is already a no-op; the sidecar goes too so
        // that a half-deleted upload cannot be listed.
        File.Delete(PathFor(id));
        File.Delete(SidecarFor(id));
        return Task.CompletedTask;
    }

    /// <summary>
    /// An id this store could have issued.
    /// </summary>
    /// <remarks>
    /// Ids come back through a URL, so by the time one is used to open a file
    /// it is caller input again. Thirty-two hex characters cannot contain a
    /// separator, a dot or a drive letter, so checking the shape is the whole
    /// of the path safety — there is no traversal left to defend against.
    /// </remarks>
    private static bool IsWellFormed(string id) =>
        id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

    private string PathFor(string id) => Path.Combine(_root, id);

    private string SidecarFor(string id) => Path.Combine(_root, $"{id}.name");
}
