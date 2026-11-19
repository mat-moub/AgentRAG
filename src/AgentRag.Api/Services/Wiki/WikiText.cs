using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRag.Api.Services;

/// <summary>A citation found in a page: a passage of a document, or a line of a repository file.</summary>
public record WikiCitation(int? DocumentId, int? Passage, int? RepositoryId, string? Path, int? Line);

/// <summary>Text conventions of wiki pages: slugs, [[links]] and source citations.</summary>
public static partial class WikiText
{
    /// <summary>Lower-case identifier without accents or punctuation: "Règles de gestion" gives "regles-de-gestion".</summary>
    public static string Slug(string text, int max = 100)
    {
        var slug = new StringBuilder();
        var separator = false;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                if (separator && slug.Length > 0)
                {
                    // The separator and the letter after it must both fit.
                    if (slug.Length + 2 > max) break;
                    slug.Append('-');
                }
                slug.Append(char.ToLowerInvariant(c));
                separator = false;
            }
            else
            {
                separator = true;
            }

            if (slug.Length >= max) break;
        }
        return slug.Length == 0 ? "page" : slug.ToString().Trim('-');
    }

    /// <summary>Targets of the [[target]] and [[target|label]] links of a page, as slugs, in order and without duplicates.</summary>
    public static IReadOnlyList<string> LinkedSlugs(string content) =>
        LinkPattern().Matches(content).Select(m => Slug(m.Groups[1].Value)).Distinct().ToList();

    /// <summary>Rewrites [[target|label]] links; a null replacement keeps only the label.</summary>
    public static string ReplaceLinks(string content, Func<string, string, string?> replace) =>
        LinkPattern().Replace(content, m =>
        {
            var label = m.Groups[2].Success ? m.Groups[2].Value.Trim() : m.Groups[1].Value.Trim();
            return replace(Slug(m.Groups[1].Value), label) ?? label;
        });

    public static IReadOnlyList<WikiCitation> Citations(string content) =>
        CitationPattern().Matches(content).Select(m => m.Groups[1].Success
            ? new WikiCitation(Number(m.Groups[1]), Number(m.Groups[2]), null, null, null)
            : new WikiCitation(null, null, Number(m.Groups[3]), Uri.UnescapeDataString(m.Groups[4].Value), Number(m.Groups[5]))).ToList();

    public static string DocumentCitation(string title, int documentId, int? passage) => passage is { } p
        ? $"[{Label(title)} · p.{p}](doc:{documentId}#p{p})"
        : $"[{Label(title)}](doc:{documentId})";

    /// <summary>Repository files are cited by path, so the citation still resolves after the file is synchronised again.</summary>
    public static string CodeCitation(int repositoryId, string path, int? line) => line is { } l
        ? $"[{Label(path)}:{l}](repo:{repositoryId}/{Uri.EscapeDataString(path).Replace("%2F", "/")}#L{l})"
        : $"[{Label(path)}](repo:{repositoryId}/{Uri.EscapeDataString(path).Replace("%2F", "/")})";

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 2]) ? max - 2 : max - 1)] + "…";

    private static string Label(string text) => text.Replace("[", "(").Replace("]", ")").Replace("\n", " ");
    private static int? Number(Group group) => group.Success && int.TryParse(group.Value, CultureInfo.InvariantCulture, out var value) ? value : null;

    // The target must contain something other than white space: "[[ ]]" is text, not a link to the page "page".
    [GeneratedRegex(@"\[\[\s*([^\[\]|\s][^\[\]|]*)(?:\|([^\[\]]+))?\]\]")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\((?:doc:(\d+)(?:#p(\d+))?|repo:(\d+)/([^)#\s]+)(?:#L(\d+))?)\)")]
    private static partial Regex CitationPattern();
}
