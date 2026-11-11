---
description: List the documents indexed in your AgentRAG knowledge base. Use to see what sources are available before searching.
argument-hint: [folderId]
allowed-tools: mcp__plugin_agentrag_agentrag__list_documents
---

List the user's indexed AgentRAG documents.

1. Call `list_documents` (`offset`, `limit` 1-100; add `folderId` if `$ARGUMENTS` gives one).
2. Show the results as a compact table of title and id. If more pages exist, say how to
   fetch the next one.
