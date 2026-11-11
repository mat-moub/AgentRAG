# Connect AgentRAG to Claude or Codex

The API serves a **Streamable HTTP MCP server** at `/mcp` using the official C# SDK. Codex supports OAuth authorization-code + PKCE with Google identification or the existing AgentRAG account. See [Google Console setup, account linking and production configuration](OAUTH.md). The `AddOAuthServer` migration adds OpenIddict tables. Legacy bearer-token clients and the local stdio bridge remain supported.

## Tools

| Tool | Purpose |
| --- | --- |
| `list_documents` | Lists your indexed documents; accepts `offset` and `limit` (1–100). |
| `search_knowledge_base` | Returns relevant passages, source titles and cosine similarity; accepts `query` and `top` (1–20). |
| `ask_rag` | Runs the existing RagAgent and its configured chat model to answer a document question. |

All tools are read-only and use the authenticated account's current access permissions, including inherited Read/Write access to shared folders. There is no client-supplied owner ID. Each tool accepts an optional `folderId` to restrict sources to that accessible folder and its descendants. Revoked shared documents disappear from subsequent requests. See [shared-folder permissions](SHARED-FOLDERS.md). `ask_rag` calls are independent: put follow-up context in the question. Search and questions accept at most 8,000 characters. Search calls the configured embedding model; `ask_rag` also calls the server's chat model, even when Claude or Codex is the client. Existing model credentials and PostgreSQL configuration must be available to the API.

## Start and sign in

Build from the repository root, then start the API with your usual configuration:

```powershell
dotnet build AgentRag.slnx -c Release
dotnet run --project src/AgentRag.Api --urls http://127.0.0.1:5000
```

For legacy clients without OAuth, in a separate PowerShell terminal (paste the URL as plain text, not a Markdown link):

```powershell
& ./scripts/Connect-RagMcp.ps1 -ApiUrl http://localhost:5000
```

The script prompts for your existing AgentRAG email and password directly in the terminal and obtains an access token from `/api/auth/login?useCookies=false`. The password is hidden as you type. It sets `AGENTRAG_MCP_TOKEN` and `AGENTRAG_MCP_URL` in that shell, without printing the token or writing credentials to disk. Launch the client from the same shell so it inherits those variables. For accounts with two-factor authentication, obtain an access token through the existing Identity login flow with the required code and set `AGENTRAG_MCP_TOKEN` yourself; this helper does not collect a second factor.

Access tokens expire according to the API's Identity settings. When calls return 401, run the helper again and restart/reconnect the client to pick up the new token. The bridge does not store passwords or refresh tokens and does not renew credentials automatically. Use HTTPS for non-loopback URLs. The bridge and helper reject non-loopback HTTP and disable redirects when transmitting credentials.

The helper uses **explicit bearer-token authentication** and is unnecessary for OAuth Codex connections. Claude Desktop can use the local bridge below. API cookies are not accepted as MCP authentication. Untrusted browser Origin headers are rejected; trusted browser origins come from `Cors:Origins`, consistent with the frontend. Dynamic OAuth registration currently supports native clients with loopback callbacks (`127.0.0.1`, `[::1]` or `localhost`), including Codex and Claude Code; hosted clients with HTTPS callbacks require a separate registration policy.

If sign-in appears stuck, press Ctrl+C. The prompts run in the terminal, with password characters hidden. The script uses 15-second network timeouts by default; override with `-TimeoutSeconds 30`. The default Visual Studio launch profile, frontend proxy and OAuth issuer all use port **5000**.

## Codex

To install AgentRAG as a **local Codex plugin**, use the bundled [local marketplace](plugins/README.md). It reuses this API and OAuth flow; no public URL or tunnel is required. The hosted **Add custom MCP server** dialog is a different connection flow and cannot use this loopback API directly.

Replace the old AgentRAG block in `~/.codex/config.toml` with [mcp-examples/codex.toml](mcp-examples/codex.toml). Remove `bearer_token_env_var` and any static Authorization header. Or register the URL:

```powershell
codex mcp add agentrag --url http://127.0.0.1:5000/mcp --oauth-resource http://127.0.0.1:5000/mcp
codex mcp login agentrag --scopes agentrag.read,offline_access
```

`mcp add` may immediately open authorization. The explicit login requests automatic refresh. Select Google or your AgentRAG login, associate your existing account if needed, then approve document access. Codex stores the resulting credentials itself. Restart/reconnect Codex after configuration changes and ask it to list documents or call `ask_rag`. The example sets a 30-minute tool timeout. See the [official Codex MCP documentation](https://developers.openai.com/codex/mcp).

## Claude Code

Merge [mcp-examples/claude-code.json](mcp-examples/claude-code.json) into your local project's `.mcp.json`. Its token reference is expanded from the environment by Claude Code; it is not a literal token. Launch `claude` from the authenticated shell and inspect `/mcp`. For lengthy provider calls, set the client timeout before launching:

```powershell
$env:MCP_TOOL_TIMEOUT = '1800000'
claude
```

See [Claude Code's MCP documentation](https://code.claude.com/docs/en/mcp) for scopes, environment expansion and timeouts.

## Claude Desktop (stdio)

Build the Release solution first. Merge [mcp-examples/claude-desktop.json](mcp-examples/claude-desktop.json) into your local Claude Desktop MCP configuration. Replace the example absolute DLL path with your checkout or published bridge path. Fully quit Claude Desktop, then launch its executable from the authenticated shell so its child bridge inherits `AGENTRAG_MCP_TOKEN`. If your launcher does not forward environment variables, set the token in the **local** server `env` configuration and replace it when it expires; never commit that credential.

The bridge connects to `AGENTRAG_MCP_URL` (default `http://localhost:5000/mcp`) and forwards tool discovery and calls, including MCP tool errors, to the API. It writes logs to stderr, keeping stdout reserved for MCP. It has no direct database/model access. Every HTTP request is authenticated by the API. The bridge needs the .NET 10 runtime.

## Hosting and verification

OAuth clients should use the canonical API `/mcp` URL configured by `OAuth:Issuer`. The default launch profile uses port 5000; change the issuer and Google callback together when overriding that port. Restart the API and Vite or rebuild the frontend container after adding these routes. Additional proxies must allow Streamable HTTP/SSE and long tool calls without buffering. The MCP transport is stateless; OAuth grants and tokens are persisted in PostgreSQL.

```powershell
dotnet test AgentRag.slnx -c Release
```

The tests exercise the SDK client, legacy bearer authentication, per-user listings and the stdio bridge. OAuth tests cover discovery, CSRF, simulated Google association, consent refusal, PKCE/code replay, audience/scope/expiry validation, refresh rotation and revocation. They use an in-memory database and simulated models/Google. A live Google/Codex smoke test requires your own configured Google client and browser sign-in.
