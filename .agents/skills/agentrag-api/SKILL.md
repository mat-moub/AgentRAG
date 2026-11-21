---
name: agentrag-api
description: Structure and conventions of the AgentRAG ASP.NET Core API (src/AgentRag.Api) — controllers and routes, DTO records, services, EF Core data model and migrations, authentication (Identity, OAuth/OpenIddict, Google), NDJSON streaming endpoints, MCP tools and the stdio bridge. Use this skill whenever adding or changing an endpoint, a controller, a DTO, an entity, a migration, an MCP tool, an option in appsettings, or anything in Program.cs, and whenever debugging a 4xx/5xx from /api or /mcp — even if the request only says "backend", "server" or "C#".
---

# AgentRAG API (`src/AgentRag.Api`)

.NET 10, nullable enabled, implicit usings, file-scoped namespaces, four-space indentation, primary
constructors for DI, `record` DTOs, `Async` suffix on async methods.

StyleCop.Analyzers and the .NET analyzers run in every build (`Directory.Build.props`, severities in
`.editorconfig`, settings in `stylecop.json`); CI builds with `-warnaserror`. Keep the build at zero warnings;
see "Linting and analyzers" in `.agents/AGENTS.md` before suppressing anything.

## Folders

| Folder | Content |
|--------|---------|
| `Program.cs` | Composition root: DbContext (Npgsql + `UseVector`), Identity API endpoints, OAuth, model clients, options validation, hosted worker, CORS, middleware order, `MapFallback` for the SPA |
| `Controllers/` | `[ApiController, Authorize, Route("api/...")]` classes; DTO records are declared at the top of each controller file |
| `Services/` | `RagService`, `EmbeddingService`, `LibraryAccess`, `RagAgentFactory`, `DocumentTextExtractor`, `RepositorySyncService`, `IRepositoryHost` (`GitHubClient`, `AzureDevOpsClient`), `JsonEventStream`, `PassageProjection`, `PassageSampling` |
| `Services/Wiki/` | Wiki pipeline — see `agentrag-llm-wiki` |
| `Data/` | `AppDbContext` (Identity + DataProtection keys), `Entities.cs`, `WikiEntities.cs`, `Migrations/`, design-time `AppDbContextFactory` |
| `Auth/` | `OAuthExtensions` (OpenIddict server, forwarded headers, cookie protection), `OAuthSettings` |
| `Mcp/` | `RagMcpTools` (tool methods) and `RagMcpExtensions` (server registration, instructions, `/mcp` mapping) |
| `Views/`, `wwwroot/` | Razor login/link/consent/public pages and their assets |

## Endpoints

| Route | Controller | Notes |
|-------|-----------|-------|
| `/api/auth/*` | Identity `MapIdentityApi` + `AccountController` | bearer tokens or cookie + CSRF (`/api/auth/session`, `/api/auth/logout`) |
| `/account/*`, `/connect/*`, `/.well-known/*` | `AccountController`, `OAuthController` | Google sign-in, OAuth dynamic client registration, authorize, token |
| `/api/library` | `LibraryController` | folders tree, revision hash, members/sharing (`read`/`write`) |
| `/api/documents` | `DocumentsController` | list, `ingest` & `upload` (NDJSON progress), `{id}/content` (NDJSON), patch, move, delete |
| `/api/repositories` | `RepositoriesController` | connect GitHub/Azure DevOps, replace token, `sync` (NDJSON), delete |
| `/api/chat`, `/api/chat/stream` | `ChatController` | JSON or NDJSON (`started`, `delta`, `completed`, `error`, heartbeat); 409 when `libraryRevision` is stale |
| `/api/explore` | `ExploreController` | passages + 2D PCA projection (≤ 500 passages) |
| `/api/wikis` | `WikiController` | CRUD, runs, run events (NDJSON), pages, search, graph, file lookup |
| `/mcp` | `RagMcpTools` | stateless HTTP MCP; tools listed below |

MCP tools: `list_documents`, `list_repositories`, `outline_document`, `grep_passages`, `read_passages`,
`search_knowledge_base`, `search_exhaustive`, `ask_rag`, `list_wikis`, `wiki_index`, `wiki_search`,
`wiki_read_page`, `wiki_graph`, `wiki_trace`. MCP answers are kept short for agents: `list_wikis` returns
`WikiOverviewDto` (freshness, no guidelines or run stats) and `wiki_read_page` returns `McpWikiPage` (capped
links and sources, with totals), not the DTOs of the web API. Their `[Description]` texts are the contract an external
agent reads: keep them precise, keep the step numbering of the default workflow consistent with
`RagMcpExtensions.ServerInstructions` and `RagAgentFactory.Instructions`.

`AgentRag.McpBridge` is a stdio proxy that forwards to `/mcp` with `AGENTRAG_MCP_TOKEN`; it never opens the
database. Tool changes in the API reach it automatically.

## Adding or changing an endpoint

1. Put the rule in a service; keep the action short (validate input → call service → return DTO).
2. Resolve the user with the existing helper pattern in the controller and load access through
   `LibraryAccess` (`Require(folderId, write: true)` for mutations). Throw `LibraryException(status, msg)`
   for 400/403/404/409; the filter returns `ProblemDetails`. Never return another user's data.
3. Wrap multi-step mutations of the library in `LibraryAccess.BeginMutationAsync` (serializable on
   PostgreSQL, no-op on the in-memory provider).
4. Long operations: `[DisableRequestTimeout]` and `JsonEventStream.WriteAsync` with a heartbeat event;
   honor `CancellationToken`; log scopes and durations, never document text, questions, answers or tokens.
5. Mirror the change in `web/src/api.ts` and its types (see `agentrag-web`), and in MCP if exposed there.
6. Add tests (see `agentrag-tests`): controller behavior via `TestServer`, access rules, SQL translation.

## Data model essentials

`KnowledgeFolder` (tree, `OwnerId`, `ParentId`) · `FolderMember` (`read`/`write`, inherited by descendants) ·
`KnowledgeDocument` (`Indexed`, `RepositoryId`, `SourcePath`, `SourceSha`, `PassageOverlap`) ·
`DocumentText` (full text, separate table) · `DocumentChunk` (`Index`, `Content`, `StartLine`,
`Embedding vector(1536)`) · `RepositoryConnection` (token encrypted with Data Protection, never returned) ·
wiki tables (`Wiki`, `WikiPage`, `WikiLink`, `WikiPageSource`, `WikiSource`, `WikiRun`).

Migrations: `dotnet ef migrations add <Name> --project src/AgentRag.Api`. Name them by intent
(`AddWiki`, `AddPassageSearchIndexes`). Development migrates on startup; production does not.

## Configuration

`ConnectionStrings:DefaultConnection`, `OpenAI:{Endpoint,ApiKey,ChatModel,EmbeddingModel,NetworkTimeoutSeconds}`,
`Ingestion:{BatchSize,MaxRetries,SaveCommandTimeoutSeconds}`, `Wiki:*`, `OAuth:*`, `Cors:Origins`.
Secrets come from user-secrets or environment variables (`OpenAI__ApiKey`), never from tracked files.
