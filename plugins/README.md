# AgentRAG local Codex plugin

This marketplace packages the existing local AgentRAG MCP server as a Codex plugin. It does not deploy the API. Keep the API running at `http://127.0.0.1:5000` with its existing Google/OAuth configuration.

From the repository root:

```powershell
codex plugin marketplace add ./plugins
codex plugin add agentrag@agentrag-local
```

Restart Codex if its plugin list has not refreshed. Select AgentRAG from the local marketplace, complete OAuth if prompted, then open a new chat and select the plugin with `@`. Ask it to list your documents before testing search or grounded questions.

The plugin contains portable manifests and a Codex compatibility manifest. Both connect to `/mcp` and contain no secrets. The icon is reused from the web application. Edit these source files, bump both manifest versions together, then reinstall through Codex; do not edit the installed cache.

The **Add custom MCP server** dialog is a separate hosted-connection flow. It requires a public HTTPS endpoint or a Secure MCP Tunnel. A loopback URL in that dialog cannot reach this local API through OpenAI's hosted services. The current OAuth registration policy also allows only native loopback callbacks. Use this local marketplace for the local plugin.
