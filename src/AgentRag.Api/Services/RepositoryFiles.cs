using System.Text;

namespace AgentRag.Api.Services;

/// <summary>Decides which repository files are mirrored and how their text is read.</summary>
public static class RepositoryFiles
{
    public const long MaxFileBytes = 1024 * 1024;

    // Prose: split like uploaded documents and embedded for semantic search.
    private static readonly HashSet<string> Documentation = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".mdx", ".markdown", ".txt", ".rst", ".adoc" };

    // Known text formats only: an unknown extension is more often a binary than something worth searching.
    private static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".fs", ".vb", ".razor", ".cshtml", ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".props", ".targets",
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte", ".astro",
        ".py", ".rb", ".go", ".rs", ".java", ".kt", ".kts", ".scala", ".groovy", ".gradle", ".php", ".swift", ".dart",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".m", ".mm", ".lua", ".pl", ".r", ".ex", ".exs", ".erl", ".clj", ".hs",
        ".sql", ".graphql", ".gql", ".proto", ".tf", ".bicep",
        ".html", ".htm", ".css", ".scss", ".sass", ".less",
        ".json", ".jsonc", ".yaml", ".yml", ".toml", ".xml", ".ini", ".cfg", ".conf", ".config", ".env.example", ".editorconfig",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd", ".http", ".feature", ".csv", ".tsv"
    };

    private static readonly HashSet<string> NamesWithoutExtension = new(StringComparer.OrdinalIgnoreCase)
        { "Dockerfile", "Makefile", "LICENSE", "README", "CHANGELOG", "CONTRIBUTING", "CODEOWNERS", "Procfile", "Jenkinsfile", ".gitignore", ".dockerignore", ".gitattributes" };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", "vendor", "packages", "coverage",
        ".git", ".vs", ".idea", ".next", ".nuxt", "__pycache__", ".venv", "venv"
    };

    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
        { "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "packages.lock.json", "composer.lock", "Cargo.lock", "poetry.lock", "Gemfile.lock", "go.sum" };

    public static bool IsDocumentation(string path) => Documentation.Contains(Path.GetExtension(path));

    /// <param name="size">Null when the host does not list sizes; the limit is then applied at download.</param>
    public static bool IsEligible(string path, long? size)
    {
        if (size is <= 0 or > MaxFileBytes) return false;
        var segments = path.Split('/');
        if (segments.SkipLast(1).Any(SkippedDirectories.Contains)) return false;
        var name = segments[^1];
        if (SkippedNames.Contains(name) || name.Contains(".min.", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".map", StringComparison.OrdinalIgnoreCase)) return false;
        var extension = Path.GetExtension(name);
        return extension.Length == 0 || extension == name
            ? NamesWithoutExtension.Contains(name)
            : Documentation.Contains(extension) || Code.Contains(extension);
    }

    /// <summary>Document title for a file: its path, keeping the end (the file name) when it exceeds the title limit.</summary>
    public static string Title(string path, int max = 256) => path.Length <= max ? path : "…" + path[^(max - 1)..];

    /// <summary>Decodes a file as UTF-8 text; null when it is binary (PostgreSQL text cannot hold NUL either).</summary>
    public static string? Decode(ReadOnlySpan<byte> content)
    {
        if (content.Contains((byte)0)) return null;
        if (content.StartsWith(Encoding.UTF8.Preamble)) content = content[Encoding.UTF8.Preamble.Length..];
        return Encoding.UTF8.GetString(content);
    }
}
