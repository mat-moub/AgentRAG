# AgentRAG for Claude Code

Claude Code plugin that connects to the AgentRAG MCP server (`https://agentrag.runasp.net/mcp`)
and adds five skills: `/agentrag:context`, `/agentrag:ask`, `/agentrag:search`, `/agentrag:story` and `/agentrag:documents`.
All tools are read-only and respect your folder permissions. No secrets are stored.

```bash
claude plugin marketplace add ./plugins/claude-code
claude plugin install agentrag@agentrag-claude
```

Then run `/mcp`, choose **agentrag** and complete the OAuth sign-in (Google or your AgentRAG account).

Try it in a new session: `/agentrag:documents`, `/agentrag:search <query>`, `/agentrag:ask <question>`.

`/agentrag:search` follows one workflow and stops as soon as the sources it has read answer:

1. `list_wikis` shows the wikis and how fresh they are.
2. When a wiki covers the subject, `wiki_search`, `wiki_read_page` and `wiki_trace` find the pages
   and give their citations as `documentId` and passage.
3. The sources:
   - for code, `outline_document` lists a file's declarations and constants, then `read_passages`
     reads only the passages that hold them;
   - for documents, `grep_passages` (literal or regular-expression match, no model call), then
     `search_knowledge_base` (vector index), and `search_exhaustive` (exact vector search over every
     passage) for in-depth research.
4. `read_passages` reads a match together with its neighbouring passages before quoting it.

When a Git repository (GitHub or Azure DevOps) is connected in the web app, its files are searchable
too:

- source code with `outline_document`, `read_passages` by path and line, and `grep_passages`
  (matches give `path`, `line` and whether the line is a declaration);
- documentation files with every level.
`/agentrag:story <need>` drafts a user story from the product documentation and that code.

The plugin always uses the hosted server (`https://agentrag.runasp.net/mcp`); `http://` requests
are redirected to HTTPS. Bump `version` in `agentrag/.claude-plugin/plugin.json` when you change
the plugin. The icon is reused from the web app.
