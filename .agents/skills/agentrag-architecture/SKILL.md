---
name: agentrag-architecture
description: Map of the AgentRAG solution (ASP.NET Core API, MCP server and stdio bridge, React web app, PostgreSQL + pgvector, LLM wiki worker) and the design rules that hold it together. Use this skill before any change that spans several layers, adds a service or a project, touches dependency injection in Program.cs, access control, streaming endpoints or configuration, or whenever someone asks "where does X live", "how is the project organized" or "how does a request flow" in AgentRAG — even if they don't say "architecture".
---

# AgentRAG architecture

AgentRAG is a retrieval-augmented research assistant: users file documents and Git repositories into
shared folders, the API splits and embeds them into PostgreSQL (pgvector), and a chat agent, an
exploration map, an LLM-compiled wiki and an MCP server all search the same authorized sources.

## Repository layout

```
AgentRag.slnx                  .NET solution (src/ + tests/)
src/AgentRag.Api/              ASP.NET Core API, MCP server, Razor sign-in pages, static web host
  Program.cs                   Composition root: DI, options validation, middleware, routes
  Auth/                        OAuth server (OpenIddict), Google sign-in, cookie/CSRF protection
  Controllers/                 HTTP endpoints, thin: validate, call a service, map to DTOs
  Data/                        AppDbContext, entities, WikiEntities, EF Core migrations
  Mcp/                         MCP tools (RagMcpTools) and server instructions (RagMcpExtensions)
  Services/                    Business logic: ingestion, retrieval, access, repositories, agent
  Services/Wiki/               LLM wiki pipeline (builder, model calls, worker, queries)
  Views/, wwwroot/             Razor pages for account/OAuth/public pages; fonts and brand assets
src/AgentRag.McpBridge/        stdio MCP proxy to the authenticated HTTP /mcp endpoint (no DB access)
tests/AgentRag.Api.Tests/      xUnit tests (in-memory EF, fake models, TestServer)
web/                           React 19 + TypeScript + Vite + Tailwind 4 front end
plugins/, submission/          Claude Code / Codex plugin packaging and marketplace review material
scripts/                       PowerShell helpers (MCP connection, plugin review/submission)
.github/workflows/deploy.yml   Builds web + API and deploys main to IIS with WebDeploy
```

Companion docs: `README.md` (features, runtime limits), `MCP.md`, `OAUTH.md`, `web/README.md`,
`web/DESIGN_SYSTEM.md`, `web/src/components/README.md`.

## Runtime picture

```
Browser (web/) ──/api──▶ Controllers ──▶ Services ──▶ AppDbContext ──▶ PostgreSQL + pgvector + pg_trgm
MCP client ─────/mcp──▶ RagMcpTools ─┘        │
McpBridge (stdio) ─HTTP─┘                     ├──▶ EmbeddingService ──▶ IEmbeddingGenerator (OpenAI/Azure)
                                              ├──▶ RagAgentFactory ──▶ IChatClient (Responses API)
WikiRunWorker (BackgroundService) ──▶ WikiBuilder ──▶ WikiModel / RagService tools
```

- One process hosts the API, the MCP endpoint, the Razor auth pages and (in production) the built SPA
  (`MapFallback` serves `wwwroot/index.html` for non-API paths).
- Models are abstracted behind `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`); the agent
  uses Microsoft Agent Framework (`Microsoft.Agents.AI`). Swap providers in `Program.cs` only.
- Long work streams NDJSON (`JsonEventStream`) with heartbeats every 10 s: ingestion, document content,
  repository sync, chat, wiki run events. Wiki builds run in a background worker, not in a request.

## Cross-cutting rules (do not break)

1. **Authorization lives in `LibraryAccess`.** Every document query goes through
   `LibrarySnapshot.Documents(db, folderId)` or `ReadableDocumentsAsync`. HTTP, ingestion, retrieval, MCP and
   the wiki all share this single policy; snapshots are loaded per request, never cached. Wiki pages are
   visible only to users who can read every source they were written from.
2. **Controllers stay thin.** Business rules belong in services; `LibraryException(status, message)` is
   turned into `ProblemDetails` by `LibraryExceptionFilter`.
3. **Options are validated at startup** (`AddOptions<T>().Bind(...).Validate(...).ValidateOnStart()`).
   New settings get defaults, bounds and a README mention. Secrets never go in tracked `appsettings*.json`.
4. **Retrieved text is untrusted data.** Agent instructions, MCP descriptions and wiki prompts all say so;
   keep that wording when editing prompts.
5. **Cancellation flows end to end.** Pass `CancellationToken` through every async call; a client disconnect
   is a normal end, not an error (`RequestCancellationMiddleware`).
6. **Schema changes are EF migrations** in `Data/Migrations` (Development applies them at startup;
   production applies them manually before deploying).

## Where to go next

| Task | Skill |
|------|-------|
| Endpoints, DTOs, auth, MCP tools, streaming | `agentrag-api` |
| Chunking, embeddings, pgvector search, grep | `agentrag-vectorization` |
| Wiki pipeline, pages, links, runs | `agentrag-llm-wiki` |
| React app structure, API client, features | `agentrag-web` |
| Tokens, components, accessibility, charts | `agentrag-design-system` |
| Writing and running tests (xUnit, Vitest) | `agentrag-tests` |

## Design principles applied here

- **Single Responsibility**: one service per concern (`EmbeddingService` only talks to the model and
  handles rate limits; `RagService` chunks, stores and searches; `LibraryAccess` decides access).
- **Open/Closed**: new Git hosts implement `IRepositoryHost` and are registered in DI; `RepositoryHosts`
  picks the right one without changing callers.
- **Liskov / Interface Segregation**: depend on small abstractions (`IChatClient`, `IEmbeddingGenerator`,
  `IRepositoryHost`) so fakes in tests are drop-in.
- **Dependency Inversion**: services receive collaborators by primary-constructor injection; only
  `Program.cs` knows concrete providers.
- Prefer pure static helpers for algorithms (`RagService.Chunk`, `CodeMap`, `WikiText`, `WikiDelta`,
  `PassageProjection`) so they are testable without a database.
