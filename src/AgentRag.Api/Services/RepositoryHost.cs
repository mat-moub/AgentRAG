using System.Text.RegularExpressions;
using AgentRag.Api.Data;

namespace AgentRag.Api.Services;

/// <param name="Size">Size in bytes when the host lists it; null when it is only known after download.</param>
public sealed record RepositoryFile(string Path, string Sha, long? Size);
public sealed record RepositoryTree(string CommitSha, IReadOnlyList<RepositoryFile> Files, bool Truncated);

/// <summary>A failure talking to a Git host, with a message that is safe to show to the user.</summary>
public sealed class RepositoryHostException(string message, int status = 502) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Read-only access to repositories of one Git host through its REST API: no clone, no local files.
/// Git blob SHAs are the same on every host, so the synchronisation compares them without knowing the host.
/// </summary>
public interface IRepositoryHost
{
    string Provider { get; }
    /// <summary>Checks the token can read the repository and returns its default branch.</summary>
    Task<string> GetDefaultBranchAsync(RepositoryAddress repository, string token, CancellationToken ct);
    Task<RepositoryTree> GetTreeAsync(RepositoryAddress repository, string branch, string token, CancellationToken ct);
    /// <summary>Downloads the given files at a commit, in whatever way is cheapest for this host. Files it cannot return are left out.</summary>
    IAsyncEnumerable<(string Path, byte[] Content)> ReadFilesAsync(RepositoryAddress repository, string commit,
        IReadOnlyList<RepositoryFile> files, string token, CancellationToken ct);
}

public sealed class RepositoryHosts(IEnumerable<IRepositoryHost> hosts)
{
    public IRepositoryHost For(string provider) => hosts.FirstOrDefault(h => h.Provider == provider)
        ?? throw new RepositoryHostException("This repository's host is not supported.", 400);
}

/// <summary>
/// A repository on a supported host, recognised from what the user typed. Only known hosts are accepted and
/// every part is validated, so the server never calls a host or path chosen freely by a user.
/// </summary>
/// <param name="Owner">GitHub owner, or Azure DevOps organization.</param>
/// <param name="Project">Azure DevOps project; null on GitHub.</param>
public sealed partial record RepositoryAddress(string Provider, string Owner, string? Project, string Name)
{
    public const string GitHub = "github";
    public const string AzureDevOps = "azuredevops";

    public string DisplayName => Project is null ? $"{Owner}/{Name}" : $"{Owner}/{Project}/{Name}";

    public static RepositoryAddress From(RepositoryConnection repository) =>
        new(repository.Provider, repository.RepositoryOwner, repository.Project, repository.RepositoryName);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex GitHubName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,49}$")]
    private static partial Regex Organization();

    /// <summary>
    /// Accepts "owner/name" and github.com addresses, and Azure DevOps Services addresses
    /// (dev.azure.com/org/project/_git/repository, its clone form with "org@", and org.visualstudio.com).
    /// Credentials embedded in a clone address are ignored: the token is given separately.
    /// </summary>
    public static bool TryParse(string? value, out RepositoryAddress address)
    {
        address = null!;
        var text = value?.Trim() ?? "";
        if (text.Length is 0 or > 500) return false;
        if (!text.Contains("://"))
        {
            var shorthand = text.Split('/');
            if (shorthand.Length == 2 && !shorthand[0].Contains('.')) return TryGitHub(shorthand, out address);
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return false;
        string[] segments;
        try { segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray(); }
        catch (UriFormatException) { return false; }
        var host = uri.Host.ToLowerInvariant();
        if (host == "github.com") return TryGitHub(segments, out address);
        if (host == "dev.azure.com" && segments.Length > 1) return TryAzureDevOps(segments[0], segments[1..], out address);
        const string legacy = ".visualstudio.com";
        if (host.EndsWith(legacy, StringComparison.Ordinal))
            return TryAzureDevOps(host[..^legacy.Length],
                segments is ["DefaultCollection", ..] ? segments[1..] : segments, out address);
        return false;
    }

    private static bool TryGitHub(string[] segments, out RepositoryAddress address)
    {
        address = null!;
        if (segments.Length != 2) return false;
        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (!GitHubName().IsMatch(segments[0]) || !GitHubName().IsMatch(name) || segments[0] is "." or ".." || name is "." or "..") return false;
        address = new(GitHub, segments[0], null, name);
        return true;
    }

    // After the organization: project/_git/repository, or _git/repository when the repository is named after its project.
    private static bool TryAzureDevOps(string organization, string[] rest, out RepositoryAddress address)
    {
        address = null!;
        var (project, name) = rest switch
        {
            [var p, "_git", var r] => (p, r),
            ["_git", var r] => (r, r),
            _ => (null, null)
        };
        if (project is null || name is null || !Organization().IsMatch(organization) || !ValidName(project) || !ValidName(name)) return false;
        address = new(AzureDevOps, organization, project, name);
        return true;
    }

    private static bool ValidName(string name) => name.Length is >= 1 and <= 100 && name is not ("." or "..") && name.Trim() == name &&
        !name.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#' or '*' or ':' or '"' or '<' or '>' or '|');
}
