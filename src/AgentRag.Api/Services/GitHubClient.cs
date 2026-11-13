using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRag.Api.Services;

public sealed record GitHubFile(string Path, string Sha, long Size);
public sealed record GitHubTree(string CommitSha, IReadOnlyList<GitHubFile> Files, bool Truncated);

/// <summary>A GitHub failure with a message that is safe to show to the user.</summary>
public sealed class GitHubException(string message, int status = 502) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Read-only access to one repository through the GitHub REST API. No clone, no local files.</summary>
public partial class GitHubClient(HttpClient http)
{
    public static readonly Uri ApiBase = new("https://api.github.com/");

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex NamePattern();

    /// <summary>Accepts "owner/name" or a github.com URL. Only GitHub-shaped names are accepted, so they are safe in a request path.</summary>
    public static bool TryParseRepository(string? value, out string owner, out string name)
    {
        (owner, name) = ("", "");
        var text = value?.Trim() ?? "";
        foreach (var prefix in new[] { "https://github.com/", "http://github.com/", "github.com/" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        var parts = text.TrimEnd('/').Split('/');
        if (parts.Length != 2) return false;
        if (parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)) parts[1] = parts[1][..^4];
        if (!NamePattern().IsMatch(parts[0]) || !NamePattern().IsMatch(parts[1]) || parts.Any(p => p is "." or "..")) return false;
        (owner, name) = (parts[0], parts[1]);
        return true;
    }

    /// <summary>Checks the token can read the repository and returns its default branch.</summary>
    public virtual async Task<string> GetDefaultBranchAsync(string owner, string name, string token, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"repos/{owner}/{name}", token, ct);
        return json.RootElement.GetProperty("default_branch").GetString()!;
    }

    public virtual async Task<GitHubTree> GetTreeAsync(string owner, string name, string branch, string token, CancellationToken ct)
    {
        using var head = await GetJsonAsync($"repos/{owner}/{name}/branches/{Uri.EscapeDataString(branch)}", token, ct);
        var commit = head.RootElement.GetProperty("commit").GetProperty("sha").GetString()!;
        using var tree = await GetJsonAsync($"repos/{owner}/{name}/git/trees/{commit}?recursive=1", token, ct);
        var files = tree.RootElement.GetProperty("tree").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .Select(e => new GitHubFile(e.GetProperty("path").GetString()!, e.GetProperty("sha").GetString()!,
                e.TryGetProperty("size", out var size) ? size.GetInt64() : 0))
            .ToList();
        return new(commit, files, tree.RootElement.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean());
    }

    public virtual async Task<byte[]> GetBlobAsync(string owner, string name, string sha, string token, CancellationToken ct)
    {
        using var response = await SendAsync($"repos/{owner}/{name}/git/blobs/{sha}", token, "application/vnd.github.raw+json", ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Streams the repository archive at a commit and yields only the requested files: one request instead of one per file.</summary>
    public virtual async IAsyncEnumerable<(string Path, byte[] Content)> ReadArchiveAsync(string owner, string name, string commit,
        string token, IReadOnlySet<string> paths, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendAsync($"repos/{owner}/{name}/tarball/{commit}", token, "application/vnd.github+json", ct);
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
        catch (HttpRequestException) { throw new GitHubException("GitHub could not be reached. Try again in a moment."); }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        var rateLimited = status == HttpStatusCode.TooManyRequests ||
            (status == HttpStatusCode.Forbidden && response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0");
        response.Dispose();
        throw rateLimited
            ? new GitHubException("GitHub's request limit for this token is reached. Try again later.", 429)
            : status switch
            {
                HttpStatusCode.Unauthorized => new GitHubException("GitHub rejected the access token. Check that it is valid and not expired.", 400),
                HttpStatusCode.Forbidden => new GitHubException("The access token is not allowed to read this repository.", 400),
                HttpStatusCode.NotFound => new GitHubException("Repository or branch not found, or the access token cannot read it.", 404),
                _ => new GitHubException("GitHub returned an unexpected error. Try again in a moment.")
            };
    }
}
