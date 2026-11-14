---
description: Find a document or passage in your AgentRAG documents. Tries a literal grep first, then the vector index, then an exhaustive vector search. Use to find or quote what a document says.
argument-hint: <term, phrase or question> [folderId]
allowed-tools: mcp__plugin_agentrag_agentrag__grep_passages, mcp__plugin_agentrag_agentrag__search_knowledge_base, mcp__plugin_agentrag_agentrag__search_exhaustive, mcp__plugin_agentrag_agentrag__read_passages, mcp__plugin_agentrag_agentrag__list_documents, mcp__plugin_agentrag_agentrag__list_repositories
---

Find `$ARGUMENTS` in the user's AgentRAG knowledge base. Search in this order and stop at the
first level that gives relevant passages.

1. **Grep** — call `grep_passages` with a short, distinctive `pattern` taken from the request:
   a term, an acronym, a reference number or an exact phrase, never the whole question.
   - Try both an acronym and its expanded form (`SAVS`, then `accompagnement à la vie sociale`).
   - Set `regex: true` for alternatives or variable spacing (`SAVS|SAMSAH`, `L\. ?311-4`).
   - `totalPassages` and `documents` show where the term is concentrated. If there are many
     matches, narrow with `documentId` or a more specific pattern rather than paging through all.
2. **Vector index** — if grep finds nothing relevant, or the request is a question with no
   distinctive wording, call `search_knowledge_base` with a self-contained `query` (`top` 1-20).
3. **Exhaustive vector search** — if level 2 misses the subject, returns scores that are all
   close together, or the question is complex and needs broad coverage, call `search_exhaustive`
   (`top` 1-50). It is slower.

Then call `read_passages` (`documentId`, `from`, `count` 1-20) around the best matches: grep
returns only an excerpt, and a single passage often cuts a list or a rule in half.

Pass `folderId` or `documentId` to any search tool when the user named a folder or document.
Documents that are not indexed have no embeddings: only grep finds them.

**Source code.** The library can hold Git repositories from GitHub or Azure DevOps (`list_repositories` gives each one's
`folderId`, branch and last synchronised commit). Their files are documents titled by file path,
and code has no embeddings, so search it with `grep_passages` only:

- grep a class, function, route, table or message name; narrow with `path` (`Controllers/*.cs`,
  `.tsx`) or the repository's `folderId`;
- find a file by name with `list_documents` and `query`;
- grep matches on code give a `line`; cite code as `path:line` and name the synchronised commit,
  because the copy can be older than the Git host.

Report each passage with its document title and passage number, best match first, and say which
level found it. Quote verbatim and attribute; do not merge passages into claims they do not
support. If no level returns anything relevant, say so.
