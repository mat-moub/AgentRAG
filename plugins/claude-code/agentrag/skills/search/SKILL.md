---
description: Search your AgentRAG documents and return the most relevant passages with source titles and similarity scores. Use to find or quote what a document says.
argument-hint: <query> [top 1-20]
allowed-tools: mcp__plugin_agentrag_agentrag__search_knowledge_base
---

Search the user's AgentRAG knowledge base for `$ARGUMENTS`.

1. Call `search_knowledge_base` with `query` (max 8,000 characters) and optionally `top`
   (1-20, default to a small number such as 5) and `folderId`.
2. Show each passage with its source title and similarity score, best match first.
3. Quote passages verbatim and attribute them; do not merge them into claims the passages
   do not support. If nothing relevant comes back, say so.
