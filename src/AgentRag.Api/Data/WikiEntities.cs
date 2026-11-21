using Pgvector;

namespace AgentRag.Api.Data;

/// <summary>
/// An LLM-maintained wiki compiled from the documents of one folder and its subfolders, repository folders included.
/// Whoever can read the folder can read the wiki; whoever can write to it can refresh it.
/// </summary>
public class Wiki
{
    public int Id { get; set; }
    public int FolderId { get; set; }
    public KnowledgeFolder? Folder { get; set; }
    /// <summary>Language the pages are written in; "auto" follows the sources.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Editorial rules given to the model on every run: glossary, naming conventions, what to emphasise.</summary>
    public string? Guidelines { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public static class WikiPageKinds
{
    public const string Overview = "overview";
    public const string Feature = "feature";
    public const string Concept = "concept";
    public const string Rule = "rule";
    public const string Module = "module";
    public const string Component = "component";
    public const string DataEntity = "data-entity";
    public const string Api = "api";
    public const string Source = "source";

    public static readonly IReadOnlyList<string> All = [Overview, Feature, Concept, Rule, Module, Component, DataEntity, Api, Source];
    /// <summary>Kinds that describe what the product does, as opposed to how the code is built.</summary>
    public static readonly IReadOnlySet<string> Functional = new HashSet<string> { Feature, Concept, Rule };
    public static readonly IReadOnlySet<string> Technical = new HashSet<string> { Module, Component, DataEntity, Api };
}

public static class WikiLinkKinds
{
    public const string Related = "related";
    public const string PartOf = "part-of";
    public const string DependsOn = "depends-on";
    public const string Implements = "implements";
    public const string Mentions = "mentions";
    public static readonly IReadOnlyList<string> All = [Related, PartOf, DependsOn, Implements, Mentions];
}

public class WikiPage
{
    public int Id { get; set; }
    public int WikiId { get; set; }
    public Wiki? Wiki { get; set; }
    /// <summary>Stable identifier inside the wiki, used by [[slug]] links. It survives refreshes.</summary>
    public string Slug { get; set; } = default!;
    public string Title { get; set; } = default!;
    /// <summary>Other names of the same subject, such as an acronym; used to avoid duplicate pages.</summary>
    public string[] Aliases { get; set; } = [];
    public string Kind { get; set; } = WikiPageKinds.Concept;
    /// <summary>One or two sentences, read from the index before a page is opened.</summary>
    public string Summary { get; set; } = "";
    /// <summary>Markdown body with [[slug]] links and doc: / repo: citations.</summary>
    public string Content { get; set; } = "";
    /// <summary>Embedding of the title and summary; null until the run's indexing stage.</summary>
    public Vector? Embedding { get; set; }
    /// <summary>True when a source of the page changed or disappeared since it was written.</summary>
    public bool Stale { get; set; }
    /// <summary>Hash of everything the page was written from; an unchanged hash means the page is not written again.</summary>
    public string InputHash { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<WikiPageSource> Sources { get; set; } = [];
}

/// <summary>A typed, directed link between two pages. Backlinks are the same rows read the other way.</summary>
public class WikiLink
{
    public int Id { get; set; }
    public int WikiId { get; set; }
    public int FromPageId { get; set; }
    public WikiPage? FromPage { get; set; }
    public int ToPageId { get; set; }
    public WikiPage? ToPage { get; set; }
    public string Kind { get; set; } = WikiLinkKinds.Related;
    /// <summary>For "implements" links: what the code does for the functional subject, with a code citation.</summary>
    public string? Note { get; set; }
}

/// <summary>Provenance: a document a page was written from. A page is only shown to users who can read all of them.</summary>
public class WikiPageSource
{
    public int Id { get; set; }
    public int PageId { get; set; }
    public WikiPage? Page { get; set; }
    /// <summary>Null once the document was deleted or replaced by a newer version.</summary>
    public int? DocumentId { get; set; }
    public KnowledgeDocument? Document { get; set; }
    /// <summary>Same key as <see cref="WikiSource.SourceKey"/>.</summary>
    public string SourceKey { get; set; } = default!;
}

/// <summary>Manifest of the sources a wiki has processed, with the cached result of their extraction.</summary>
public class WikiSource
{
    public int Id { get; set; }
    public int WikiId { get; set; }
    public Wiki? Wiki { get; set; }
    /// <summary>"repo:{repositoryId}:{path}" for a repository file, so a new version replaces the old one; "doc:{id}" otherwise.</summary>
    public string SourceKey { get; set; } = default!;
    public int? DocumentId { get; set; }
    public KnowledgeDocument? Document { get; set; }
    /// <summary>Git blob SHA for repository files, document id otherwise: a different value means the source changed.</summary>
    public string Fingerprint { get; set; } = default!;
    /// <summary>"document" or "code".</summary>
    public string Type { get; set; } = "document";
    /// <summary>JSON produced by the extraction stage; null until the source, or its code module, was extracted.</summary>
    public string? Extraction { get; set; }
    public DateTime ProcessedUtc { get; set; } = DateTime.UtcNow;
}

public static class WikiRunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public static bool IsActive(string status) => status is Queued or Running;
}

public static class WikiRunModes
{
    public const string Incremental = "incremental";
    public const string Full = "full";
    public const string Lint = "lint";
    public static readonly IReadOnlyList<string> All = [Incremental, Full, Lint];
}

/// <summary>One build of a wiki, executed in the background. The rows are also the wiki's history.</summary>
public class WikiRun
{
    public int Id { get; set; }
    public int WikiId { get; set; }
    public Wiki? Wiki { get; set; }
    public string Mode { get; set; } = WikiRunModes.Incremental;
    public string Status { get; set; } = WikiRunStatus.Queued;
    public string Stage { get; set; } = "queued";
    public int Completed { get; set; }
    public int Total { get; set; }
    /// <summary>When the current stage started, to estimate the time it has left from its pace so far.</summary>
    public DateTime? StageStartedUtc { get; set; }
    /// <summary>Items already done when the current stage started, such as sources extracted before a restart.</summary>
    public int StageStartCompleted { get; set; }
    /// <summary>The run reads and writes with this user's access, checked again at each save.</summary>
    public string RequestedById { get; set; } = default!;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string? Error { get; set; }
    /// <summary>JSON summary: sources processed, pages created, updated and removed, lint findings.</summary>
    public string? Stats { get; set; }
}
