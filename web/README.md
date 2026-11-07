# AgentRAG web

A mobile-first React and TypeScript interface for the AgentRAG API, built with Vite and Tailwind CSS 4. All interface copy is English. Fonts, logo and icons are bundled locally.

## Local development

Requires Node.js 22.12+ and the running .NET API (see the root README).

```sh
npm ci
cp .env.example .env
npm run dev
```

Open `http://127.0.0.1:5173`. `API_PROXY_TARGET` defaults to `http://127.0.0.1:5000`, matching the API launch profile and OAuth issuer. Set it in `.env` only if your API uses another port; update the OAuth issuer and Google callback together when changing that port. Requests use same-origin `/api` by default. `VITE_API_URL` is an optional public, build-time override for a separate API origin; configure API CORS when using it. Never put model keys or database credentials in `VITE_*` variables.

```sh
npm test        # Login, document CRUD, chat retry, and loading/error workflows
npm run lint    # Oxlint
npm run build   # TypeScript check and static production output in dist/
npm run preview
```

The tests use mocked API responses; they do not need PostgreSQL with pgvector or model credentials.

## Customize a fork

Start with [DESIGN_SYSTEM.md](./DESIGN_SYSTEM.md). Edit the semantic theme in `src/index.css`, shared primitives in `src/components/`, and feature modules in `src/features/`. Replace `public/brand/mark.png` and the favicon files to rebrand. There is no runtime dependency on a CDN, remote font provider or design service.

The API client stays in `src/api.ts`. Chat messages are session-local and disappear on reload or sign-out. The backend currently returns an answer string with document-title citations; the UI renders Markdown without executing HTML or loading remote images. Source titles are not fabricated into clickable excerpts.

## Deploy the frontend

`npm run build` produces `dist/`, which can be served by any static host. Route `/api/*` to the .NET API through your reverse proxy. Keep the API behind the same origin to avoid environment-specific frontend builds.

A production frontend container is included:

```sh
docker build -t agentrag-web .
docker run --rm -p 8080:80 -e API_UPSTREAM=http://host.docker.internal:5000 agentrag-web
```

`API_UPSTREAM` is injected into the Nginx proxy at container startup, so one frontend image can target different API deployments. In Compose, use the API service address (for example `http://api:8080`). On Linux, connecting to a host API may require `--add-host=host.docker.internal:host-gateway` and an API listening on an address reachable from Docker.

This image serves only the frontend. Deploy the .NET API, PostgreSQL with pgvector and model configuration separately. Terminate HTTPS at your ingress, configure production CORS if needed, and apply EF migrations as a deployment step; automatic migrations only run in Development.
