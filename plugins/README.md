# AgentRAG local Codex plugin

This local marketplace packages a Codex plugin that connects to the AgentRAG MCP server at `https://agentrag.runasp.net/mcp`. It does not deploy the API.

From the repository root:

```powershell
codex plugin marketplace add ./plugins
codex plugin add agentrag@agentrag-local
```

Restart Codex if its plugin list has not refreshed. Select AgentRAG from the local marketplace, complete OAuth if prompted, then open a new chat and select the plugin with `@`. Ask it to list your documents before testing search or grounded questions.

The plugin contains portable manifests and a Codex compatibility manifest. Both connect to `/mcp` and contain no secrets. The icon is reused from the web application. Edit these source files, bump both manifest versions together, then reinstall through Codex; do not edit the installed cache.

The **Add custom MCP server** dialog is a separate hosted-connection flow. The current OAuth registration policy allows only native loopback callbacks; use this local marketplace for the Codex plugin.
