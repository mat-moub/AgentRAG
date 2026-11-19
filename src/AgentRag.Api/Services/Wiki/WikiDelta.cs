using System.Globalization;
using AgentRag.Api.Data;

namespace AgentRag.Api.Services;

/// <summary>A document inside the wiki's scope, reduced to what identifies it and its version.</summary>
public record ScopeDocument(int Id, string Title, int? RepositoryId, string? SourcePath, string? SourceSha);

/// <param name="Changed">Manifest rows whose document was replaced by a newer version.</param>
/// <param name="Relinked">Rows with an unchanged version that point to another document id.</param>
public record WikiDeltaResult(IReadOnlyList<ScopeDocument> Added, IReadOnlyList<(WikiSource Row, ScopeDocument Document)> Changed,
    IReadOnlyList<WikiSource> Removed, IReadOnlyList<(WikiSource Row, ScopeDocument Document)> Relinked, int Unchanged);

/// <summary>Compares the documents in scope with the manifest of what the wiki already processed.</summary>
public static class WikiDelta
{
    public const string Document = "document";
    public const string Code = "code";
    public const string Module = "module";

    /// <summary>
    /// Documents are never edited in place: a changed repository file is deleted and added again under a new id.
    /// Keying repository files by path lets the wiki see that as a new version rather than a removal and an addition.
    /// </summary>
    public static string Key(ScopeDocument document) => document is { RepositoryId: { } repository, SourcePath: { } path }
        ? $"repo:{repository}:{path}"
        : $"doc:{document.Id}";

    public static string Fingerprint(ScopeDocument document) =>
        document.SourceSha is { Length: > 0 } sha && document.RepositoryId is not null ? sha : document.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Repository files that are not prose are code: they are described per module, not per file.</summary>
    public static string Type(ScopeDocument document) =>
        document is { RepositoryId: not null, SourcePath: { } path } && !RepositoryFiles.IsDocumentation(path) ? Code : Document;

    /// <param name="manifest">Rows of files and documents; module rows are derived from them and compared separately.</param>
    public static WikiDeltaResult Compute(IEnumerable<ScopeDocument> documents, IEnumerable<WikiSource> manifest)
    {
        // A repository path normally has one document; if a failed synchronisation left two, the newest wins.
        var current = documents.GroupBy(Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.MaxBy(d => d.Id)!, StringComparer.Ordinal);
        var added = new List<ScopeDocument>();
        var changed = new List<(WikiSource, ScopeDocument)>();
        var relinked = new List<(WikiSource, ScopeDocument)>();
        var removed = new List<WikiSource>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var unchanged = 0;
        foreach (var row in manifest)
        {
            if (!known.Add(row.SourceKey) || !current.TryGetValue(row.SourceKey, out var document)) removed.Add(row);
            else if (row.Fingerprint != Fingerprint(document)) changed.Add((row, document));
            else if (row.DocumentId != document.Id) relinked.Add((row, document));
            else unchanged++;
        }
        added.AddRange(current.Where(d => !known.Contains(d.Key)).Select(d => d.Value).OrderBy(d => d.Id));
        return new(added, changed, removed, relinked, unchanged + relinked.Count);
    }
}
