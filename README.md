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
3. `cd web && npm install && npm run dev` (API URL via `VITE_API_URL`, default `http://localhost:5000`).
