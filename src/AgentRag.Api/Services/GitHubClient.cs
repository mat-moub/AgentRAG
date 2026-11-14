using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AgentRag.Api.Services;

/// <summary>Read-only access to one repository through the GitHub REST API. No clone, no local files.</summary>
public class GitHubClient(HttpClient http) : IRepositoryHost
{
    public static readonly Uri ApiBase = new("https://api.github.com/");
    /// <summary>Above this many files to fetch, one archive download replaces one request per file.</summary>
    public const int ArchiveThreshold = 40;

    public string Provider => RepositoryAddress.GitHub;

    public virtual async Task<string> GetDefaultBranchAsync(RepositoryAddress repository, string token, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"repos/{repository.Owner}/{repository.Name}", token, ct);
        return json.RootElement.GetProperty("default_branch").GetString()!;
    }

    public virtual async Task<RepositoryTree> GetTreeAsync(RepositoryAddress repository, string branch, string token, CancellationToken ct)
    {
        var root = $"repos/{repository.Owner}/{repository.Name}";
        using var head = await GetJsonAsync($"{root}/branches/{Uri.EscapeDataString(branch)}", token, ct);
        var commit = head.RootElement.GetProperty("commit").GetProperty("sha").GetString()!;
        using var tree = await GetJsonAsync($"{root}/git/trees/{commit}?recursive=1", token, ct);
        var files = tree.RootElement.GetProperty("tree").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .Select(e => new RepositoryFile(e.GetProperty("path").GetString()!, e.GetProperty("sha").GetString()!,
                e.TryGetProperty("size", out var size) ? size.GetInt64() : 0))
            .ToList();
        return new(commit, files, tree.RootElement.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean());
    }

    public async IAsyncEnumerable<(string Path, byte[] Content)> ReadFilesAsync(RepositoryAddress repository, string commit,
        IReadOnlyList<RepositoryFile> files, string token, [EnumeratorCancellation] CancellationToken ct)
    {
        if (files.Count > ArchiveThreshold)
        {
            var wanted = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            await foreach (var file in ReadArchiveAsync(repository, commit, token, wanted, ct)) yield return file;
            yield break;
        }
        foreach (var file in files) yield return (file.Path, await GetBlobAsync(repository, file.Sha, token, ct));
    }

    public virtual async Task<byte[]> GetBlobAsync(RepositoryAddress repository, string sha, string token, CancellationToken ct)
    {
        using var response = await SendAsync($"repos/{repository.Owner}/{repository.Name}/git/blobs/{sha}", token, "application/vnd.github.raw+json", ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Streams the repository archive at a commit and yields only the requested files: one request instead of one per file.</summary>
    public virtual async IAsyncEnumerable<(string Path, byte[] Content)> ReadArchiveAsync(RepositoryAddress repository, string commit,
        string token, IReadOnlySet<string> paths, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendAsync($"repos/{repository.Owner}/{repository.Name}/tarball/{commit}", token, "application/vnd.github+json", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null) continue;
            // Entries are prefixed with one directory named after the repository and commit.
            var slash = entry.Name.IndexOf('/');
            var path = slash < 0 ? entry.Name : entry.Name[(slash + 1)..];
            if (!paths.Contains(path) || entry.Length > RepositoryFiles.MaxFileBytes) continue;
            using var buffer = new MemoryStream((int)entry.Length);
            await entry.DataStream.CopyToAsync(buffer, ct);
            yield return (path, buffer.ToArray());
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, string token, CancellationToken ct)
    {
        using var response = await SendAsync(path, token, "application/vnd.github+json", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string token, string accept, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.ParseAdd("AgentRAG");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new RepositoryHostException("GitHub could not be reached. Try again in a moment."); }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        var rateLimited = status == HttpStatusCode.TooManyRequests ||
            (status == HttpStatusCode.Forbidden && response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0");
        response.Dispose();
        throw rateLimited
            ? new RepositoryHostException("GitHub's request limit for this token is reached. Try again later.", 429)
            : status switch
            {
                HttpStatusCode.Unauthorized => new RepositoryHostException("GitHub rejected the access token. Check that it is valid and not expired.", 400),
                HttpStatusCode.Forbidden => new RepositoryHostException("The access token is not allowed to read this repository.", 400),
                HttpStatusCode.NotFound => new RepositoryHostException("Repository or branch not found, or the access token cannot read it.", 404),
                _ => new RepositoryHostException("GitHub returned an unexpected error. Try again in a moment.")
            };
    }
}
