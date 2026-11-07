# Google authentication and MCP OAuth

Google identifies the user; AgentRAG issues its own OAuth access and refresh tokens for the MCP. The Google password stays with Google. Google access tokens are not accepted by the MCP and are not saved by AgentRAG.

## Google Cloud Console

1. Open [Google Cloud Console](https://console.cloud.google.com/) and create or select an **AgentRAG** project.
2. Under **Google Auth Platform → Branding**, enter the application name, support email and contact email.
3. Under **Audience**, choose **External** for personal Gmail accounts. Stay in testing and add your email under **Test users**. Organization-only Workspace apps may use Internal when available.
4. Under **Data Access**, select only `openid`, `email` and `profile`. No Gmail or Drive API access is needed.
5. Under **Clients → Create client**, choose **Web application**, named `AgentRAG local`.
6. Add this exact authorized redirect URI: `http://127.0.0.1:5000/signin-google`. No JavaScript origin is needed for the server-side flow. The Codex callback is registered with AgentRAG, not Google.
7. Store the Client ID and Client secret locally:

```powershell
dotnet user-secrets set "Authentication:Google:ClientId" "YOUR_CLIENT_ID" --project src/AgentRag.Api
dotnet user-secrets set "Authentication:Google:ClientSecret" "YOUR_CLIENT_SECRET" --project src/AgentRag.Api
```

Restart the API after configuring secrets. Without Google configuration, local email/password OAuth still works and Google buttons are hidden. For production, create a separate Google client with the deployed HTTPS `/signin-google` callback. See [Google's server-side OAuth guide](https://developers.google.com/identity/protocols/oauth2/web-server).

## Local startup and existing accounts

```powershell
dotnet build AgentRag.slnx -c Release
dotnet run --no-build -c Release --project src/AgentRag.Api --urls http://127.0.0.1:5000
```

The Development launch profile applies the `AddOAuthServer` migration automatically. It adds OpenIddict client, scope, authorization and token tables without changing document ownership. Migration generation uses a design-time factory that does not contact Google or a database. To apply migrations explicitly with EF CLI, supply your real connection with `--connection`; the factory's default connection is only a local design-time placeholder.

Open **http://127.0.0.1:5173/**. Use the same hostname consistently: `localhost` and `127.0.0.1` do not share cookies. Vite defaults to API port 5000; an existing `API_PROXY_TARGET` override must target this same API instance.

Choose **Continue with Google**. If the Google identity is unknown, choose either **Associate with my existing account** (requires the AgentRAG email/password) or explicitly create a new empty account. Email matching never automatically associates accounts. Existing users can also choose **Associate Google** in the workspace. That flow requires a fresh password check and must complete within five minutes; a Google identity cannot belong to two users. Documents keep their original owner ID.

Existing two-factor accounts are rejected by these new browser password/external forms, rather than bypassing a second factor. A dedicated two-factor browser flow is not included; the existing Identity API two-factor login remains available.

Google web sessions use an HttpOnly SameSite=Lax cookie. `/api/auth/session` supplies session state and a request CSRF token. Cookie-authenticated API mutations, streaming requests and logout require `X-CSRF-TOKEN`; existing bearer requests remain supported. Signing out of the website does not revoke Codex grants.

## Codex

Replace your old AgentRAG config with [the OAuth example](mcp-examples/codex.toml). Remove `bearer_token_env_var` and any static Authorization header, then run:

```powershell
codex mcp login agentrag --scopes agentrag.read,offline_access
```

Select Google, associate your account if necessary, then approve access. Codex receives AgentRAG credentials and stores them in its own credential storage. With `--no-browser`, open the printed URL and paste the final callback URL into the waiting terminal, even if that callback page cannot load. Treat this one-time callback URL as a credential; do not commit it or paste it into documentation.

The read-only `agentrag.read` scope allows listing, searching and asking questions against the authenticated user's documents. Requests may send passages to the model configured in AgentRAG. `offline_access` requests refresh tokens. Access tokens last one hour; refresh tokens last 30 days and rotate with replay rejection. Token and authorization status are checked in PostgreSQL on MCP calls. The token endpoint also rejects deleted/locked-out users and changed security stamps. Logout from a client and token revocation are separate operations; `/connect/revoke` supports revoking tokens issued to that client.

## Endpoints and deployment

| Endpoint | Purpose |
| --- | --- |
| `/.well-known/oauth-protected-resource` | MCP resource and issuer metadata; also exposed at `/.well-known/oauth-protected-resource/mcp` |
| `/.well-known/oauth-authorization-server` | OpenIddict OAuth metadata, including registration endpoint |
| `/.well-known/openid-configuration` | OpenIddict discovery |
| `/connect/register` | Public native-client registration; literal loopback redirects only, 16 KiB request limit, ten requests/minute |
| `/connect/authorize` | Login redirect and explicit consent, with PKCE S256 required |
| `/connect/token` | Code exchange and rolling refresh tokens |
| `/connect/revoke` | Token revocation |

Local defaults are `OAuth:Issuer=http://127.0.0.1:5000/` and `OAuth:SiteUrl=http://127.0.0.1:5173/`. The MCP resource is the issuer plus `/mcp`. Account routes must be accessed at the issuer origin, which also keeps the Google callback stable.

For production:

- Set the OAuth issuer/site URL to their public HTTPS origins. HTTP is allowed only for Development loopback. Configure a separate Google Web client with the exact production HTTPS callback.
- Set `VITE_AUTH_URL` to the public OAuth issuer origin, and `VITE_API_URL` if the API uses a different origin. The production frontend defaults to same-origin account routes. Keep site/API on the same site for SameSite cookies; configure exact CORS origins with credentials.
- Configure persistent signing/encryption PFX files using `OAuth:SigningCertificatePath`, `OAuth:SigningCertificatePassword`, `OAuth:EncryptionCertificatePath` and `OAuth:EncryptionCertificatePassword`. Production fails without certificates. Development uses persistent user-store certificates.
- Persist ASP.NET Core Data Protection keys across restarts/replicas for cookies and legacy Identity tokens. Supply file/certificate passwords through secrets or environment variables.
- The bundled Nginx config forwards account, callback, OAuth and metadata routes. Preserve the public Host and real HTTPS scheme. Configure only trusted proxy IPs in `OAuth:KnownProxies`; do not accept arbitrary forwarded headers.
- Schedule pruning of expired tokens/authorizations and unused dynamically registered clients for long-running deployments. This version supports Codex/native loopback registration, not arbitrary hosted HTTPS callback clients.

## Verification

Run `dotnet test AgentRag.slnx -c Release`, then `npm run build`, `npm run lint` and `npm test` in `web/`. OAuth integration tests simulate Google via its backchannel and use an in-memory database. They cover account association/conflicts, preserved document ownership, CSRF, discovery, consent, PKCE/code replay, wrong audience, missing scope, expiry, refresh rotation and revocation. Verify the real Google/Codex browser flow separately with configured credentials; then call `list_documents` and confirm that only your own documents appear.
