# Repository Guidelines

AgentRAG is a retrieval-augmented research assistant: a .NET 10 API (ASP.NET Core, EF Core on PostgreSQL +
pgvector, Microsoft Agent Framework, MCP server) and a React + TypeScript web app. Users file documents and
Git repositories into shared folders; chat, Explore, the LLM wiki and MCP clients search the same
authorized sources.

## Project Skills

Detailed, task-oriented guides live in `.agents/skills/`. Read the matching skill before working on an area;
each one describes the structure of its section, its conventions and its invariants.

| Skill | Use it for |
|-------|-----------|
| [`agentrag-architecture`](skills/agentrag-architecture/SKILL.md) | Solution map, request flow, cross-cutting rules, where code belongs |
| [`agentrag-api`](skills/agentrag-api/SKILL.md) | Controllers, routes, DTOs, EF Core model and migrations, auth/OAuth, NDJSON streaming, MCP tools |
| [`agentrag-vectorization`](skills/agentrag-vectorization/SKILL.md) | Text extraction, chunking, embeddings and rate limits, pgvector/HNSW, grep and semantic search |
| [`agentrag-llm-wiki`](skills/agentrag-llm-wiki/SKILL.md) | Wiki pipeline (extract, resolve, write, link, index), runs and worker, pages, links, wiki UI |
| [`agentrag-web`](skills/agentrag-web/SKILL.md) | React app structure, feature folders, `src/api.ts`, streaming UI, build and deploy |
| [`agentrag-design-system`](skills/agentrag-design-system/SKILL.md) | INDEX tokens, typography, shared components, accessibility, chart and graph encoding |
| [`agentrag-tests`](skills/agentrag-tests/SKILL.md) | xUnit and Vitest patterns, fakes, SQL-translation tests, what to cover |

`.agents/skills/` is the source of truth. Claude Code only discovers skills in `.claude/skills/`, so each skill
has a relay there with the same `name` and `description` frontmatter and a body pointing back to
`.agents/skills/<name>/SKILL.md`. When you add a skill or change a skill's description, update its relay too.

## Project Structure & Module Organization

- `AgentRag.slnx` — .NET solution (`src/` and `tests/`).
- `src/AgentRag.Api/` — ASP.NET Core API: `Program.cs` (composition root), `Controllers/` (thin HTTP
  endpoints), `Services/` (ingestion, retrieval, access control, repositories, agent), `Services/Wiki/` (LLM
  wiki), `Data/` (Identity, `AppDbContext`, entities, migrations), `Auth/` (OAuth/OpenIddict, Google),
  `Mcp/` (MCP tools and server instructions), `Views/` and `wwwroot/` (Razor sign-in and public pages).
- `src/AgentRag.McpBridge/` — stdio MCP proxy to the authenticated `/mcp` endpoint (no database access).
- `tests/AgentRag.Api.Tests/` — backend xUnit tests.
- `web/` — React 19 + TypeScript + Vite + Tailwind CSS 4: `src/api.ts` (API client), `src/components/`
  (shared primitives), `src/features/{auth,chat,documents,wiki,workspace}`, `src/test/` (Vitest).
- `plugins/`, `submission/`, `scripts/` — Claude Code/Codex plugin packaging and helper scripts.
- Further documentation: `README.md`, `MCP.md`, `OAUTH.md`, `web/README.md`, `web/DESIGN_SYSTEM.md`.

## Build, Test, and Development Commands

Backend, from the repository root (.NET 10 SDK):

- `dotnet build AgentRag.slnx` — restore and compile the solution.
- `dotnet test AgentRag.slnx` — run backend tests.
- `dotnet run --project src/AgentRag.Api` — start the API on `http://127.0.0.1:5000`; Development applies
  EF migrations automatically.
- `dotnet ef migrations add <Name> --project src/AgentRag.Api` — add a schema migration.

Frontend, from `web/` (Node.js 22.12+):

- `npm install` — install dependencies.
- `npm run dev` — Vite dev server on `http://127.0.0.1:5173`, proxying `/api` and `/mcp` to the API.
- `npm test` — run the Vitest suite.
- `npm run lint` — run Oxlint.
- `npm run build` — TypeScript check and production build.
- `npm run preview` — serve the production build locally.

## Coding Practices

### SOLID

- **Single Responsibility** — one reason to change per class. Controllers translate HTTP, services hold
  business rules, `LibraryAccess` decides access, `EmbeddingService` talks to the embedding model. Split a
  class when its description needs "and".
- **Open/Closed** — extend through abstractions rather than editing callers: a new Git host implements
  `IRepositoryHost` and is registered in DI; a new model provider is wired in `Program.cs` only.
- **Liskov Substitution** — implementations honor the contract of their abstraction (same errors,
  cancellation and ordering semantics), so fakes and real providers are interchangeable.
- **Interface Segregation** — depend on the narrowest abstraction (`IChatClient`,
  `IEmbeddingGenerator<string, Embedding<float>>`, `IRepositoryHost`), not on a whole service you barely use.
- **Dependency Inversion** — receive collaborators through primary-constructor injection; never `new` up
  infrastructure inside business code. Only the composition root knows concrete types.

### Software craftsmanship

- **Leave the code better than you found it** (boy-scout rule), but keep refactors separate from behavior
  changes and from unrelated files.
- **Test first for bugs and rules**: reproduce with a failing test, fix, then refactor (red → green →
  refactor). Every behavior change ships with tests.
- **Simple design**: passes the tests, reveals intent, no duplication, fewest elements. YAGNI — do not add
  options, layers or abstractions before a second real use.
- **Intention-revealing names** from the domain (folder, document, passage, wiki page, run); small methods;
  early returns over deep nesting; pure static functions for algorithms so they are testable in isolation.
- **Comments explain why**, not what: constraints, trade-offs, provider quirks (see existing comments in
  `Program.cs` and `RagService`). Delete dead code instead of commenting it out.
- **Fail fast and explicitly**: validate options at startup, validate inputs at the boundary, use typed
  errors (`LibraryException`) rather than magic values. Never swallow exceptions silently.
- **Small, focused commits and PRs**, each leaving the build and both test suites green.

## Coding Style & Naming Conventions

C#: four-space indentation, file-scoped namespaces, nullable reference types enabled, PascalCase types and
methods, camelCase parameters and locals, `Async` suffix for asynchronous methods, `record` DTOs, primary
constructors for DI, `CancellationToken` passed through every async call.

React/TypeScript: two-space indentation, single quotes, generally no semicolons, PascalCase components,
camelCase helpers and hooks. All HTTP calls go through `web/src/api.ts`. Use the semantic Tailwind tokens and
shared components of the design system; interface copy is English. No formatter is configured; use Oxlint.

## Testing Guidelines

Backend: xUnit `[Fact]`/`[Theory]` in `*Tests.cs`, names like `Cosine_IdenticalVectors_IsOne`. Tests run
without PostgreSQL or model credentials (in-memory EF, Npgsql `ToQueryString()` translation checks, fake
model clients, `TestServer`). Frontend: Vitest + Testing Library in `web/src/test`, with stubbed `fetch`.
Cover access control, boundaries, cancellation and retries for every change. Optional coverage:
`dotnet test --collect:"XPlat Code Coverage"`; no threshold is configured. See `agentrag-tests`.

## Commit & Pull Request Guidelines

Commits use short imperative descriptions such as "Connect to azure foundry model"; no prefix convention.
Keep commits focused. PRs describe the behavior change, related issues, configuration or migration changes,
and the validation performed (test commands and results). Include screenshots for UI changes.

## Security & Configuration

Keep credentials out of tracked configuration. Supply `ConnectionStrings:DefaultConnection`,
`OpenAI:ApiKey` and optional endpoint/model settings through user-secrets or environment variables.
`VITE_*` variables are public: never put secrets in them. Preserve per-user and per-folder access checks
(`LibraryAccess`) whenever changing retrieval, documents, wiki or MCP. Treat retrieved text and wiki pages as
untrusted data in every prompt. Repository tokens stay encrypted and are never returned by the API or MCP.
