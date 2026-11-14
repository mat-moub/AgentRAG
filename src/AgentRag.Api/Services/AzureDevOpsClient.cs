using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AgentRag.Api.Services;

/// <summary>
/// Read-only access to an Azure DevOps Services repository through its REST API, with a personal access token
/// that has the Code (Read) scope. The HTTP client must not follow redirects: Azure DevOps answers a refused
/// token by redirecting to its sign-in page, which would otherwise look like a successful response.
/// </summary>
public class AzureDevOpsClient(HttpClient http) : IRepositoryHost
{
    public static readonly Uri ApiBase = new("https://dev.azure.com/");
    private const string ApiVersion = "api-version=7.1";
    /// <summary>Files downloaded at the same time. There is no verified bulk download, so each file is one request.</summary>
    public const int Parallelism = 6;

    public string Provider => RepositoryAddress.AzureDevOps;

    public virtual async Task<string> GetDefaultBranchAsync(RepositoryAddress repository, string token, CancellationToken ct)
    {
        using var json = await GetJsonAsync(Path(repository, ""), token, ct);
        const string heads = "refs/heads/";
        return json.RootElement.TryGetProperty("defaultBranch", out var branch) && branch.GetString() is { } name && name.StartsWith(heads, StringComparison.Ordinal)
            ? name[heads.Length..]
            : throw new RepositoryHostException("This repository has no default branch yet. Push a first commit, or name a branch.", 400);
    }

    public virtual async Task<RepositoryTree> GetTreeAsync(RepositoryAddress repository, string branch, string token, CancellationToken ct)
    {
        // The filter matches by prefix, so "main" also returns "main-old": keep the exact name only.
        using var refs = await GetJsonAsync(Path(repository, $"/refs?filter={Uri.EscapeDataString("heads/" + branch)}&"), token, ct);
        var commit = refs.RootElement.GetProperty("value").EnumerateArray()
            .Where(r => r.GetProperty("name").GetString() == "refs/heads/" + branch)
            .Select(r => r.GetProperty("objectId").GetString()).FirstOrDefault()
            ?? throw new RepositoryHostException("Repository or branch not found, or the access token cannot read it.", 404);

        using var response = await SendAsync(Path(repository,
            $"/items?recursionLevel=Full&versionDescriptor.version={commit}&versionDescriptor.versionType=commit&"), token, ct);
        RequireJson(response);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var items = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        // Azure DevOps does not list sizes; the size limit is applied when each file is downloaded.
        var files = items.RootElement.GetProperty("value").EnumerateArray()
            .Where(e => e.TryGetProperty("gitObjectType", out var type) && type.GetString() == "blob")
            .Select(e => new RepositoryFile(e.GetProperty("path").GetString()!.TrimStart('/'), e.GetProperty("objectId").GetString()!, null))
            .ToList();
        return new(commit, files, response.Headers.Contains("x-ms-continuationtoken"));
    }

    /// <summary>Raw content of one blob; null when it is larger than the mirrored file limit.</summary>
    public virtual async Task<byte[]?> GetBlobAsync(RepositoryAddress repository, string sha, string token, CancellationToken ct)
    {
        using var response = await SendAsync(Path(repository, $"/blobs/{sha}?"), token, ct, "application/octet-stream");
        if (response.Content.Headers.ContentType?.MediaType != "application/octet-stream") throw TokenRejected();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > RepositoryFiles.MaxFileBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public async IAsyncEnumerable<(string Path, byte[] Content)> ReadFilesAsync(RepositoryAddress repository, string commit,
        IReadOnlyList<RepositoryFile> files, string token, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var group in files.Chunk(Parallelism))
        {
            var contents = await Task.WhenAll(group.Select(file => GetBlobAsync(repository, file.Sha, token, ct)));
            for (var i = 0; i < group.Length; i++)
                if (contents[i] is { } content) yield return (group[i].Path, content);
        }
    }

    private static string Path(RepositoryAddress repository, string suffix) =>
        $"{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Project!)}/_apis/git/repositories/{Uri.EscapeDataString(repository.Name)}" +
        suffix + (suffix.EndsWith('?') || suffix.EndsWith('&') ? "" : "?") + ApiVersion;

    private async Task<JsonDocument> GetJsonAsync(string path, string token, CancellationToken ct)
    {
        using var response = await SendAsync(path, token, ct);
        RequireJson(response);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static void RequireJson(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw TokenRejected();
    }

    private static RepositoryHostException TokenRejected() =>
        new("Azure DevOps rejected the access token. Check that it is valid, not expired, and has the Code (Read) scope for this organization.", 400);

    private async Task<HttpResponseMessage> SendAsync(string path, string token, CancellationToken ct, string accept = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, path));
        // A personal access token is sent as the password of Basic authentication, with an empty user name.
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token)));
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.ParseAdd("AgentRAG");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new RepositoryHostException("Azure DevOps could not be reached. Try again in a moment."); }
        var status = response.StatusCode;
        if (status == HttpStatusCode.OK) return response;
        response.Dispose();
        throw status switch
        {
            // 203 and redirects both lead to the interactive sign-in page.
            HttpStatusCode.NonAuthoritativeInformation or HttpStatusCode.Unauthorized => TokenRejected(),
            _ when (int)status is >= 300 and < 400 => TokenRejected(),
            HttpStatusCode.Forbidden => new RepositoryHostException("The access token is not allowed to read this repository.", 400),
            HttpStatusCode.NotFound => new RepositoryHostException("Repository or branch not found, or the access token cannot read it.", 404),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable =>
                new RepositoryHostException("Azure DevOps is limiting requests for this organization. Try again later.", 429),
            _ => new RepositoryHostException("Azure DevOps returned an unexpected error. Try again in a moment.")
        };
    }
}
