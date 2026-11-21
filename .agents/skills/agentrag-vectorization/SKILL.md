---
name: agentrag-vectorization
description: How AgentRAG turns documents and repository files into searchable passages and vectors — text extraction, chunking (prose vs line-based code), embedding batches with rate-limit retries, pgvector storage with HNSW and pg_trgm indexes, and the three retrieval levels (grep, indexed vector search, exhaustive scan) plus read_passages and the Explore PCA map. Use this skill for any change to ingestion, chunk size or overlap, embedding model or dimensions, similarity search, ranking, grep, passage numbering, or search performance, and when diagnosing "search doesn't find X", 429 quota errors or slow queries — even if the user just says "RAG", "embeddings", "index" or "search".
---

# Vectorization and retrieval

All of it lives in `src/AgentRag.Api/Services/` (`RagService`, `EmbeddingService`, `DocumentTextExtractor`,
`RepositorySyncService`, `PassageProjection`, `PassageSampling`) and `Data/AppDbContext.cs` (indexes).

## Pipeline

```
upload / paste / repository file
  └─ DocumentTextExtractor   PDF (PdfPig, page by page), TXT, MD, CSV; UTF-8 or UTF-16 BOM; 10 MB, 2M chars
       └─ RagService.Chunk       prose: 800 chars, 100 overlap  (PassageOverlap = 100)
          RagService.ChunkLines  code:  ≤ 1500 chars on line ends, no overlap, StartLine set (PassageOverlap = 0)
            └─ EmbeddingService.GenerateAsync   batches of Ingestion:BatchSize (16), one process-wide gate
                 └─ DocumentChunk.Embedding     vector(1536) — text-embedding-3-small
                      └─ single SaveChanges at the end (nothing persisted until every batch succeeded)
```

- Every document gets passages (so it can be grepped and read); only `Indexed` documents get embeddings.
  Repository source code is stored unindexed; documentation files (Markdown, text) are embedded.
- Full text is kept in `DocumentText`; `ReconstructText` removes overlaps when reading passages back.
- Progress events: `chunking` → `embedding` (completed/total) → `rateLimited` (retryAt, attempt) → saved.

## Embedding service rules

- One `SemaphoreSlim` serializes embedding calls per process (ingestion and query embeddings share it).
- HTTP 429 is retried up to `Ingestion:MaxRetries`, honoring `retry-after-ms`, `x-ms-retry-after-ms`,
  `Retry-After` (seconds or date); fallback is exponential from 60 s, capped at 300 s, plus jitter.
- `insufficient_quota` / `billing_hard_limit` are permanent: never retried.
- SDK retries are disabled (`ClientRetryPolicy(0)`) so the UI can show waits. Keep it that way.
- Changing the embedding model or dimension requires a migration of `vector(N)`
  (`AppDbContext.EmbeddingDimensions`) and re-ingesting every document — say so explicitly in the PR.

## Storage and indexes (PostgreSQL)

- Extensions `vector` and `pg_trgm` (created by migrations).
- `DocumentChunks.Embedding`: HNSW with `vector_cosine_ops`. `DocumentChunks.Content`: GIN `gin_trgm_ops`.
- Wiki pages have the same pair of indexes (embedding of title + summary, content trigram).

## Retrieval levels (`RagService`)

| Level | Method | MCP tool | How |
|-------|--------|----------|-----|
| Literal / regex | `GrepAsync` / `GrepChunks` | `grep_passages` | `ILIKE` with `EscapeLike`, or POSIX regex with `RegexOptions.Multiline` (`^`/`$` at line ends); `statement_timeout = 15s`; returns totals, top documents (≤ 20), excerpts, line for code, `definition` for declaration lines (moved first among the first 200 matches) and a `hint` above 50 unnarrowed matches; `path` matches the title or `SourcePath` |
| Indexed semantic | `SearchAsync(exact: false)` | `search_knowledge_base` | `ORDER BY embedding <=> query LIMIT top` with `SET LOCAL hnsw.iterative_scan = relaxed_order` |
| Exhaustive semantic | `SearchAsync(exact: true)` | `search_exhaustive` | same query with `SET LOCAL enable_indexscan = off` (exact scan) |
| Read | `ReadPassagesAsync` | `read_passages` | consecutive passages, overlap removed, first and last line for code; can start at the passage holding a line, and take a path (`ResolvePathAsync`) |
| Outline | `OutlineAsync` | `outline_document` | `CodeMap.Outline` for code (declarations, constants, routes) or Markdown headings, each with its line and passage; no embedding |

Code and wiki pages get no passage embeddings on purpose: code is reached by grep, outlines and the wiki's
resolved citations; a wiki page has a single embedding of its title and summary.

`PassageOverlap` has no database default: with one, EF Core leaves out the CLR default 0 of code files and
PostgreSQL stores the default, which made `read_passages` cut 100 characters at each seam of code.

Performance invariants, protected by SQL-translation tests in `RagServiceTests`:
- Accessible document ids are passed as a materialized list (`"DocumentId" = ANY (...)`), not a subquery,
  otherwise PostgreSQL sorts every passage and the HNSW index is skipped.
- The ORDER BY of the indexed search must be the distance only.
- Scores are `1 - cosine distance`: similarity, not confidence.
- Passage numbers exposed to users and agents are one-based (`Index + 1`).

## Explore map

`ExploreController` samples at most 500 passages in stable id order (`PassageSampling`), projects them in 2D
with power-iteration PCA (`PassageProjection`), and scores with full-vector cosine (`RagService.Cosine`).

## When changing something here

- Keep access filtering (`LibraryAccess`) in front of every query — retrieval is where leaks happen.
- Add boundary tests: empty text, text exactly `ChunkSize`, last chunk shorter than overlap, very long single
  line, surrogate pairs, CRLF, regex special characters, 429 headers parsing (`RetryDelay` is `internal`).
- Re-check the generated SQL with `ToQueryString()` when touching LINQ in search methods.
