# Repository Guidelines

## Project Structure & Module Organization

`AgentRag.slnx` contains the .NET solution. `src/AgentRag.Api/` hosts the ASP.NET Core API: `Controllers/` handles chat and document endpoints, `Services/` implements chunking, retrieval, and agent creation, and `Data/` contains Identity entities, the EF Core context, and migrations. `tests/AgentRag.Api.Tests/` holds backend unit tests. `web/` contains the React + TypeScript Vite application; UI components, API helpers, and styles live in `web/src/`.

## Build, Test, and Development Commands

Run backend commands from the repository root with the .NET 10 SDK installed:

- `dotnet build AgentRag.slnx` — restore dependencies and compile the solution.
- `dotnet test AgentRag.slnx` — run backend tests.
- `dotnet run --project src/AgentRag.Api --urls http://localhost:5000` — start the API; Development mode applies database migrations automatically.

Run frontend commands from `web/`:

- `npm install` — install dependencies.
- `npm run dev` — start the Vite development server.
- `npm run build` — check TypeScript and produce a production build.
- `npm run lint` — run Oxlint.
- `npm run preview` — serve the production build locally.

## Coding Style & Naming Conventions

Match existing code: C# uses four-space indentation, file-scoped namespaces, PascalCase types and methods, camelCase parameters and locals, and `Async` suffixes for asynchronous service methods. Keep nullable reference annotations enabled. React/TypeScript uses two-space indentation, single-quoted strings, and generally omits semicolons. Name components in PascalCase and helpers in camelCase. No dedicated formatter configuration is checked in; use Oxlint for frontend linting.

## Testing Guidelines

Use xUnit `[Fact]` tests in `*Tests.cs` files. Follow descriptive names such as `Cosine_IdenticalVectors_IsOne`. Cover changed retrieval and chunking behavior, including boundary cases. Existing unit tests run without SQL Server or model credentials. Collect optional coverage with `dotnet test --collect:"XPlat Code Coverage"`; no coverage threshold or frontend test suite is configured.

## Commit & Pull Request Guidelines

The short Git history uses imperative descriptions such as “Connect to azure foundry model”; no formal prefix convention is established. Keep commits focused. PRs should describe the behavior change, relevant issues, configuration or migration changes, and validation performed. Include screenshots for UI changes.

## Security & Configuration

Keep credentials out of tracked configuration. Supply `ConnectionStrings:DefaultConnection`, `OpenAI:ApiKey`, and optional endpoint/model settings through environment variables or local secrets. Configure `VITE_API_URL` for the frontend. Preserve per-user document ownership checks when changing retrieval or document access.
