# AgentRAG
An ai agent rag in .NET using Agent Framework

# AgentRAG

RAG chat agent built with .NET 10, Microsoft Agent Framework, EF Core (SQL Server), ASP.NET Core Identity and a React (Vite) front end.

- `src/AgentRag.Api` – .NET 10 API (Identity bearer-token endpoints under `/api/auth`, documents `/api/documents`, chat `/api/chat`). Documents are chunked, embedded (OpenAI) and stored in SQL Server; the agent uses a `search_knowledge_base` tool for retrieval.
- `web` – React + TypeScript UI.
- `tests` – xunit tests.

## Run
1. Set `ConnectionStrings:DefaultConnection` and `OpenAI:ApiKey` (e.g. `dotnet user-secrets`).
2. `dotnet run --project src/AgentRag.Api --urls http://localhost:5000` (migrations are applied automatically in Development).
3. Set `API_PROXY_TARGET=http://localhost:5000` in `web/.env`, then `cd web && npm install && npm run dev`.

## Web interface

The mobile-first INDEX interface uses Tailwind CSS 4 and shared React components. See [web/README.md](web/README.md) for development, frontend tests and container deployment, and [web/DESIGN_SYSTEM.md](web/DESIGN_SYSTEM.md) for theming and branding a fork. The default frontend uses same-origin `/api` requests; configure `API_PROXY_TARGET` in `web/.env` to match the API port (the launch profile uses `5096`).
