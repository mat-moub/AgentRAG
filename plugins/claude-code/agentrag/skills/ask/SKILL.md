---
description: Ask a question answered from your AgentRAG documents, with source citations. Use when the user wants an answer grounded in their own indexed documents.
argument-hint: <question> [folderId]
allowed-tools: mcp__plugin_agentrag_agentrag__ask_rag
---

Answer the user's question from their AgentRAG knowledge base.

1. Call `ask_rag` with the question in `$ARGUMENTS`. Calls are independent, so fold any needed
   follow-up context into the question text (max 8,000 characters).
2. If the user named a folder, pass its `folderId` to restrict the sources.
3. Present the answer and list the cited sources (document titles). If the tool reports no
   relevant passages, say so instead of answering from general knowledge.

`ask_rag` runs the server's chat model, so it can take a while; wait for it.
