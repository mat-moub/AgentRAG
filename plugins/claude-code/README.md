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

`/agentrag:search` looks for a passage in three levels and stops at the first one that answers:
`grep_passages` (literal or regular-expression match, no model call), `search_knowledge_base`
(vector index), then `search_exhaustive` (exact vector search over every passage).
`read_passages` then reads a match together with its neighbouring passages.

The plugin always uses the hosted server (`https://agentrag.runasp.net/mcp`); `http://` requests
are redirected to HTTPS. Bump `version` in `agentrag/.claude-plugin/plugin.json` when you change
the plugin. The icon is reused from the web app.
