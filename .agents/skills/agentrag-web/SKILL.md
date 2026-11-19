---
name: agentrag-web
description: Structure and conventions of the AgentRAG React front end (web/) — Vite 8, React 19, TypeScript, Tailwind CSS 4, feature folders (auth, chat, documents, wiki, workspace), the single API client in src/api.ts with NDJSON streaming, session/CSRF handling, dev proxy and Docker/Nginx deployment. Use this skill for any change in web/: a new screen or component, a new API call, a hook, streaming UI, routing between panels, build/lint errors, Vite proxy or environment variables — even if the user only says "frontend", "UI", "React" or "the page".
---

# AgentRAG web (`web/`)

Mobile-first SPA. All interface copy is English. Two-space indentation, single quotes, no semicolons,
PascalCase components, camelCase helpers and hooks (`useX`).

## Layout

```
web/
  index.html, vite.config.ts      Vite + React + Tailwind plugin; dev proxy /api and /mcp → API_PROXY_TARGET (timeouts off)
  vitest.config.ts                jsdom, src/test/setup.ts
  .oxlintrc.json                  correctness/suspicious/perf + react, typescript, unicorn, import, jsx-a11y, vitest
  Dockerfile, nginx.conf.template Static image; API_UPSTREAM injected at start; buffering off for streams
  public/                         brand/, favicons, preview images (no runtime CDN)
  src/
    main.tsx, App.tsx             session bootstrap → Login or Workspace
    api.ts                        THE API client: types, request(), streaming helpers, every endpoint
    index.css                     design tokens (@theme) — see agentrag-design-system
    components/                   shared primitives: ui.tsx (Button, IconButton, Field, Notice, Dialog, Select),
                                  Select.tsx, FolderSelect.tsx, Brand, ProgressRing, SiteFooter
    features/
      auth/        Login (email/password, Google)
      chat/        Chat (streamed Markdown answer, retry, history scoped to library revision)
      documents/   Library tree, Documents (add/upload), IngestionProgress, DocumentReader,
                   RepositoryDialog, useLibrary, useDocuments, ingestionTiming
      wiki/        WikiHome, WikiBrowser, WikiPage, WikiGraph, wikiMarkdown, wikiGraphStyle, wikiProgress
      workspace/   Workspace (layout, sidebar, scope), Explore (semantic map)
    test/          Vitest + Testing Library suites, one per flow or helper
```

## API client rules (`src/api.ts`)

- Every HTTP call lives here; components never call `fetch` directly. Add the TypeScript interface next to
  the function and keep it aligned with the C# DTO (camelCase JSON).
- `request<T>()` sets JSON headers, bearer token or `X-CSRF-TOKEN` for cookie sessions, `credentials: 'include'`.
- `checkResponse()` maps errors to friendly messages (never shows server HTML), dispatches
  `agentrag:unauthorized` on 401 and `agentrag:access-changed` on 403/404/stale-revision 409.
- Streaming endpoints (chat, ingest/upload, document content, repository sync, wiki run events) read
  NDJSON line by line, accept an `AbortSignal`, and ignore heartbeats. No client-side timeout timers.
- Base URL is same-origin `/api`; `VITE_API_URL` is an optional public build-time override. Never put
  secrets in `VITE_*`.

## Component conventions

- Feature components own their state; shared logic goes into hooks (`useLibrary`, `useDocuments`) or pure
  helpers (`ingestionTiming.ts`, `wikiMarkdown.ts`) that are unit-tested.
- Use primitives from `components/ui.tsx` and `Select`/`FolderSelect` instead of native `<select>`;
  icons from `@phosphor-icons/react` (regular weight).
- Render model output with `react-markdown` without raw HTML or remote images; never fabricate citations.
- Abort in-flight requests in effect cleanups (`let active = true` / `AbortController`).
- Keep layouts mobile-first, then `sm:`/`lg:` (documents become a sidebar at 1024 px).

## Commands (from `web/`)

```bash
npm install
npm run dev      # http://127.0.0.1:5173, API on 127.0.0.1:5000
npm test         # Vitest, mocked fetch, no backend needed
npm run lint     # Oxlint --deny-warnings (rules in .oxlintrc.json)
npm run build    # tsc -b + vite build → dist/
```

Before finishing a web change run `npm run lint`, `npm test` and `npm run build`; for visible changes,
check the screen at mobile and desktop widths and attach screenshots to the PR.
