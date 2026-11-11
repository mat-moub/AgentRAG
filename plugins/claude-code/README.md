# AgentRAG for Claude Code

Claude Code plugin that connects to the AgentRAG MCP server (`https://agentrag.runasp.net/mcp`)
and adds three skills: `/agentrag:ask`, `/agentrag:search` and `/agentrag:documents`.
All tools are read-only and respect your folder permissions. No secrets are stored.

```bash
claude plugin marketplace add ./plugins/claude-code
claude plugin install agentrag@agentrag-claude
```

Then run `/mcp`, choose **agentrag** and complete the OAuth sign-in (Google or your AgentRAG account).

Try it in a new session: `/agentrag:documents`, `/agentrag:search <query>`, `/agentrag:ask <question>`.

To test against a local API instead, change `url` in `agentrag/.mcp.json` to
`http://127.0.0.1:5000/mcp` (see [MCP.md](../../MCP.md)). Bump `version` in
`agentrag/.claude-plugin/plugin.json` when you change the plugin. The icon is reused from the web app.
