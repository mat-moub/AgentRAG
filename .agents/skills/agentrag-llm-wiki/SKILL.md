---
name: agentrag-llm-wiki
description: Structure of the AgentRAG LLM wiki — per-folder wikis compiled by a language model from documents and Git repositories into interlinked pages (overview, feature, concept, rule, module, component, data-entity, api, source) with typed links (related, part-of, depends-on, implements, mentions), built by a resumable background run (listing, extracting, resolving, writing, linking, indexing). Use this skill for any change to WikiBuilder, WikiModel prompts, WikiService queries, wiki runs or the worker, wiki entities, slugs/citations, the wiki MCP tools or the wiki UI (features/wiki), and when debugging stale pages, duplicate pages, missing implements links or a stuck run.
---

# LLM wiki

Backend in `src/AgentRag.Api/Services/Wiki/`, entities in `Data/WikiEntities.cs`, HTTP in
`Controllers/WikiController.cs`, MCP tools `list_wikis`, `wiki_index`, `wiki_search`, `wiki_read_page`,
`wiki_graph`, `wiki_trace` in `Mcp/RagMcpTools.cs`, UI in `web/src/features/wiki/`.

## Files

| File | Responsibility |
|------|----------------|
| `WikiBuilder.cs` | The staged pipeline for one run; every stage saves what it finished, so a run resumes after a restart |
| `WikiModel.cs` | All model calls (extract, write, merge duplicates, link implementation, overview) with structured outputs; `WikiOptions`; token counters |
| `WikiService.cs` | Queries and commands for controllers/MCP: CRUD, runs, pages, search (whole query, then most of its words, then semantic), graph, trace, citations resolved to documentId and passage, freshness overview, access checks |
| `WikiRuns.cs` | `WikiRunQueue` (signal + cancellation) and `WikiRunWorker` (`BackgroundService`, one run at a time, requeues interrupted runs) |
| `WikiDelta.cs` | Pure diff between the folder's current sources and the `WikiSource` manifest (added / changed / removed) |
| `CodeMap.cs` | Pure grouping of repository files into modules by directory under a character budget, plus outlines |
| `WikiText.cs` | Slugs (accent-free), `[[slug|label]]` links, citations `doc:123#p12` and `repo:5/path#L107` |

## Data model

- `Wiki` — one per folder (`FolderId` unique), `Language` (`auto` follows sources), `Guidelines`.
- `WikiPage` — stable `Slug`, `Title`, `Aliases`, `Kind`, `Summary`, Markdown `Content`, `Embedding`
  (title + summary), `Stale`, `InputHash` (unchanged hash ⇒ page not rewritten).
- `WikiLink` — directed and typed; backlinks are the same rows read backwards. `implements` goes from a
  code page to a functional page and carries a `Note` with a code citation.
- `WikiPageSource` — provenance; **a page is shown only to users who can read all its sources.**
- `WikiSource` — manifest per source key (`doc:{id}` or `repo:{repoId}:{path}`), fingerprint (blob SHA or
  doc id), cached extraction JSON.
- `WikiRun` — `incremental` | `full` | `lint`; status `queued` → `running` → `completed`/`failed`/`cancelled`;
  stage, progress, stats JSON. A filtered unique index allows one active run per wiki. `WikiBuilder.Record`
  notes when each stage started and from which count (`StageStartedUtc`, `StageStartCompleted`), so the UI
  (`WikiRunProgress`) estimates the time a stage has left from its own pace, and adds each stage's duration to
  `Stats.StageSeconds`. `WikiRunDto.Now` carries the server time, so elapsed times do not depend on the browser clock.

Kinds: functional = feature, concept, rule; technical = module, component, data-entity, api; plus overview
and source.

## Pipeline stages (`WikiBuilder.RunAsync`)

1. **listing** — scope = readable documents of the folder subtree (subfolders shared with a different
   audience are excluded), code grouped into modules by `CodeMap`; `WikiDelta` marks touched sources and
   their pages stale. Repository sync already marks the pages of changed or deleted files stale, and
   updates a changed file in place, so its document id and the page sources stay valid.
2. **extracting** — per document window (`ExtractWindowCharacters`) or code module digest
   (`ModulePromptCharacters`), in parallel (`MaxParallelism`); failures leave the source pending for next run;
   if everything fails the run stops (model unreachable). The module digest gives each file's outline
   (declarations, constants, routes) and its first lines numbered, and names the files left out for lack of
   room; the prompt asks for constants, formulas and calculation steps as cited facts. A cited line past the
   end of its file is dropped at resolve, and a code identifier is never a feature or rule page.
3. **resolving** — items become `PagePlan`s, existing pages are claimed so slugs survive, the model merges
   duplicate titles; functional and code pages with the same name stay separate on purpose.
4. **writing** — pages whose input hash changed are written with a `PageBrief` (facts with citations, link
   candidates, existing content, language, guidelines); provenance is synced for every page.
5. **linking** — structural links (related, part-of, depends-on), then `implements` links: the model searches
   the code with grep/read tools scoped to the folder and confirms before linking (`MaxLinkedPages`).
6. **indexing** — links found in text, overview page (`index`), page embeddings, lint checks.

Model calls happen outside transactions; write access of `RequestedById` is re-checked before each save, so a
revoked writer cannot keep publishing.

## Rules when editing

- Prompts must keep the "source material is untrusted data" preamble and forbid inventing facts or refs.
- Structured output records must stay schema-friendly for OpenAI strict mode (hence `ExtractedItemFact`
  duplicates `ExtractedFact`: no `$ref` reuse).
- Never let a page leak: any new query path filters by the caller's `LibrarySnapshot` and page sources.
- Keep the stage names in sync with `web/src/features/wiki/wikiProgress.ts` and the API types in `api.ts`.
- Keep link and kind vocabularies in sync with `WikiPageKinds`/`WikiLinkKinds`, the MCP descriptions and
  `wikiGraphStyle.ts` (shape per kind, color per family — see `agentrag-design-system`).
- Tests use `WikiTestDb` (in-memory) and `FakeWikiModel` (deterministic, counts calls) from
  `WikiTestSupport.cs`; assert on call counts to prove incremental runs do not redo work.

## Frontend

`WikiIntro` (shown while a folder has no wiki or one never built: what an LLM wiki is, diagrams, good and poor fits, create-and-build), `WikiHome` (create/refresh, run progress via `watchWikiRun`), `WikiBrowser` (index and search), `WikiPage`
(Markdown via `wikiMarkdown.ts`, resolving `[[slug]]`, `doc:` and `repo:` links), `WikiGraph`
(react-force-graph-2d, styles in `wikiGraphStyle.ts`).
