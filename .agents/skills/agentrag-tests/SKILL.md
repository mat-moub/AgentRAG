---
name: agentrag-tests
description: How AgentRAG is tested — backend xUnit suite in tests/AgentRag.Api.Tests (in-memory EF Core, PostgreSQL SQL-translation tests, fake IChatClient / IEmbeddingGenerator / WikiModel, TestServer integration, FakeTimeProvider) and frontend Vitest + Testing Library suite in web/src/test (jsdom, stubbed fetch, NDJSON streams). Use this skill whenever writing, fixing or reviewing tests, adding a feature or bug fix that needs coverage, running the test commands, or investigating a failing test — even if the user only says "add coverage", "TDD" or "is this tested?".
---

# Testing AgentRAG

Tests must run without PostgreSQL, network, model credentials or a browser. Write the test first when fixing
a bug (red → green → refactor) and keep each test focused on one behavior.

## Commands

```bash
dotnet test AgentRag.slnx                                  # backend
dotnet test AgentRag.slnx --filter "FullyQualifiedName~RagServiceTests"
dotnet test AgentRag.slnx --collect:"XPlat Code Coverage"  # optional coverage
cd web && npm test                                         # frontend (vitest run)
cd web && npx vitest run src/test/library.test.tsx         # single file
```

## Backend (`tests/AgentRag.Api.Tests`)

xUnit `[Fact]` (and `[Theory]` for tables of cases), one `*Tests.cs` per unit or feature, names
`Method_Condition_Expectation` (e.g. `SearchChunks_IndexedSearch_OrdersByDistanceOnlySoHnswIndexApplies`).
`InternalsVisibleTo` exposes `internal` helpers to tests.

| Need | Pattern | Example |
|------|---------|---------|
| Pure algorithm | call the static method | `RagService.Chunk`, `ChunkLines`, `CodeMap`, `WikiText`, `WikiDelta`, `PassageProjection` |
| SQL shape / index usage | `UseNpgsql("Host=localhost;Database=unused", o => o.UseVector())` + `.ToQueryString()`; no DB connection is opened | `RagServiceTests`, `WikiQueryTranslationTests` |
| Data behavior | `UseInMemoryDatabase(uniqueName)`; ignore `DocumentChunk.Embedding` in a derived context (no vector type in memory) | `WikiTestDb`, `RepositorySyncTests.TestDb` |
| Model calls | small private fakes implementing `IChatClient` / `IEmbeddingGenerator<string, Embedding<float>>`; `FakeWikiModel` for the wiki (deterministic, counts calls) | `ChatStreamingTests`, `IngestionTests`, `WikiTestSupport` |
| HTTP / auth / MCP end to end | build the app with `WebHost.UseTestServer()`, replace `AppDbContext` with in-memory, register fakes | `McpTests`, `OAuthTests`, `SharedFoldersTests` |
| Time and retries | `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing) | `IngestionTests` (22-minute call, heartbeats) |
| Git hosts | fake `HttpMessageHandler` responses for GitHub / Azure DevOps | `RepositorySyncTests` |

What to cover for every change:
- **Access control** — a user without rights gets 403/404 and sees nothing (documents, passages, wiki pages,
  MCP results). This is the most important regression class in this codebase.
- **Boundaries** — empty input, exact size limits, last chunk, overlap, long lines, Unicode surrogates,
  pagination limits, invalid options.
- **Cancellation and failures** — client disconnects, 429 retries, permanent quota errors, partial runs that
  must resume without redoing work (assert call counts on fakes).
- **SQL invariants** — keep `"DocumentId" = ANY (` and distance-only ORDER BY assertions when touching search.

## Frontend (`web/src/test`)

Vitest + jsdom + Testing Library (`@testing-library/react`, `user-event`, `jest-dom`). `setup.ts` cleans up,
unstubs globals, clears `localStorage`, and polyfills `scrollIntoView`, `<dialog>` and `matchMedia`.

- Mock the network with `vi.stubGlobal('fetch', vi.fn().mockImplementation(url => ...))`; return
  `new Response(JSON.stringify(value), { status })` or NDJSON bodies for streams
  (`JSON.stringify(event) + '\n'` per event).
- Query by role and accessible name (`screen.getByRole('button', { name: 'Add document' })`): it tests
  accessibility at the same time. Prefer `userEvent` over `fireEvent` for interactions.
- Pure helpers (`ingestionTiming`, `wikiMarkdown`, `wikiGraphStyle`) get plain unit tests.
- One file per flow or helper: `flows`, `library`, `explore`, `select`, `chat-api`, `ingestion-api`,
  `wiki-api`, `oauth-api`, `auth-availability`, …

## Quality bar

- Arrange / Act / Assert, no logic in tests, no sleeps (use fake time or `waitFor`).
- Tests describe behavior, not implementation details; a refactor should not break them.
- A fix ships with the test that would have caught it. Run both suites before opening a PR and state the
  result in the PR description.
