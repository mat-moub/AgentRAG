using System.Text;
using System.Text.RegularExpressions;

namespace AgentRag.Api.Services;

public record CodeFile(string Path, int DocumentId, string Fingerprint, int Length);

/// <param name="Key">Directory of the module, "" for the repository root; "#2" and so on when a directory was split.</param>
public record CodeModule(string Key, IReadOnlyList<CodeFile> Files)
{
    public string Name => Key.Length == 0 ? "(root)" : Key;
}

/// <summary>
/// Groups the files of a repository into modules and outlines them without a model call, so the model
/// describes a few modules rather than thousands of files.
/// </summary>
public static partial class CodeMap
{
    /// <summary>
    /// A directory small enough is one module. A larger one becomes one module for its own files and one per
    /// subdirectory, recursively; a directory without subdirectories that is still too large is cut into parts.
    /// </summary>
    /// <param name="budget">Maximum characters of source per module.</param>
    public static IReadOnlyList<CodeModule> Modules(IEnumerable<CodeFile> files, int budget)
    {
        var modules = new List<CodeModule>();
        Split("", files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList());
        return modules;

        void Split(string directory, List<CodeFile> under)
        {
            if (under.Count == 0) return;
            var prefix = directory.Length == 0 ? 0 : directory.Length + 1;
            var direct = under.Where(f => f.Path.IndexOf('/', prefix) < 0).ToList();
            if (direct.Count == under.Count || under.Sum(f => (long)f.Length) <= budget)
            {
                Emit(directory, under);
                return;
            }
            Emit(directory, direct);
            foreach (var child in under.Where(f => f.Path.IndexOf('/', prefix) >= 0)
                         .GroupBy(f => f.Path[..f.Path.IndexOf('/', prefix)], StringComparer.Ordinal))
                Split(child.Key, child.ToList());
        }

        void Emit(string directory, List<CodeFile> members)
        {
            var part = new List<CodeFile>();
            var (size, number) = (0L, 1);
            foreach (var file in members)
            {
                if (part.Count > 0 && size + file.Length > budget)
                {
                    modules.Add(new(number == 1 ? directory : $"{directory}#{number}", part));
                    (part, size, number) = ([], 0, number + 1);
                }
                part.Add(file);
                size += file.Length;
            }
            if (part.Count > 0) modules.Add(new(number == 1 ? directory : $"{directory}#{number}", part));
        }
    }

    /// <summary>Changes whenever a file of the module is added, removed or modified.</summary>
    public static string Fingerprint(CodeModule module) =>
        WikiText.Hash(string.Join('\n', module.Files.Select(f => f.Path + ":" + f.Fingerprint)))[..40];

    /// <summary>Declarations and routes of a file with their one-based line numbers.</summary>
    public static IReadOnlyList<(int Line, string Text)> Outline(string text, int max = 40)
    {
        var outline = new List<(int, string)>();
        var line = 0;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            line++;
            if (raw.Length is < 4 or > 400) continue;
            var value = raw.ToString();
            if (!Declaration().IsMatch(value) && !Route().IsMatch(value)) continue;
            outline.Add((line, WikiText.Truncate(value.Trim(), 160)));
            if (outline.Count == max) break;
        }
        return outline;
    }

    /// <summary>What the model reads for one module: per file, its outline and as much of its start as the budget allows.</summary>
    public static string Digest(CodeModule module, IReadOnlyDictionary<int, string> texts, int budget)
    {
        var perFile = Math.Clamp(budget / Math.Max(module.Files.Count, 1), 600, 12_000);
        var digest = new StringBuilder();
        foreach (var file in module.Files)
        {
            if (!texts.TryGetValue(file.DocumentId, out var text)) continue;
            var lines = text.AsSpan().Count('\n') + 1;
            digest.Append("### ").Append(file.Path).Append(" (").Append(lines).AppendLine(" lines)");
            var outline = Outline(text);
            if (outline.Count > 0)
            {
                digest.AppendLine("Outline:");
                foreach (var (number, declaration) in outline) digest.Append("  ").Append(number).Append(": ").AppendLine(declaration);
            }
            // The outline already costs characters; what is left shows the start of the file.
            var room = perFile - outline.Sum(o => o.Text.Length + 8);
            if (room >= 200)
            {
                digest.AppendLine(text.Length <= room ? "Content:" : "Start of the content:");
                digest.AppendLine(WikiText.Truncate(text, room));
            }
            digest.AppendLine();
            if (digest.Length > budget * 2) break;
        }
        return digest.ToString();
    }

    [GeneratedRegex(@"^\s*(?:(?:public|internal|private|protected|export|static|abstract|sealed|partial|async|default|final|open|override|virtual|readonly|declare|pub)\s+)*(?:class|interface|record|struct|enum|namespace|module|trait|impl|type|func|function|def|fn|object)\s+[A-Za-z_][\w.<>,]*")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"^\s*(?:\[(?:Http(?:Get|Post|Put|Delete|Patch)|Route|ApiController|McpServerTool)\b|@(?:app|router|blueprint)\.\w+\(|@(?:Get|Post|Put|Delete|Patch|RequestMapping|Controller)\b|(?:app|router|group|endpoints)\.(?:get|post|put|delete|patch|Map\w*)\(|(?:public|internal)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],.?]+\s+\w+\s*\()", RegexOptions.IgnoreCase)]
    private static partial Regex Route();
}
