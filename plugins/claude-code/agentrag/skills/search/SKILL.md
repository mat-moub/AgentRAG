---
description: Find a document, passage or piece of code in your AgentRAG library. Checks the wikis first, then reads the sources they cite, outlines code files, and falls back to a literal grep, the vector index and an exhaustive vector search. Use to find or quote what a document or the code says.
argument-hint: <term, phrase or question> [folderId]
allowed-tools: mcp__plugin_agentrag_agentrag__list_wikis, mcp__plugin_agentrag_agentrag__wiki_search, mcp__plugin_agentrag_agentrag__wiki_read_page, mcp__plugin_agentrag_agentrag__wiki_trace, mcp__plugin_agentrag_agentrag__outline_document, mcp__plugin_agentrag_agentrag__grep_passages, mcp__plugin_agentrag_agentrag__search_knowledge_base, mcp__plugin_agentrag_agentrag__search_exhaustive, mcp__plugin_agentrag_agentrag__read_passages, mcp__plugin_agentrag_agentrag__list_documents, mcp__plugin_agentrag_agentrag__list_repositories
---

Find `$ARGUMENTS` in the user's AgentRAG knowledge base.

Go from the wiki down to the sources, read as little as possible, and stop as soon as the sources you
have read answer. About six calls answer most requests.

**1. Inventory.** Call `list_wikis`: it gives each wiki's `folderId` and freshness. For a question
about code, also call `list_repositories` (each repository's `folderId` and synchronised commit). Use
`list_documents` only to find a document or file by name.

**2. Wiki, when one covers the subject.** Call `wiki_search` with a few distinctive words or an
identifier, not the whole question. Open the best page with `wiki_read_page`, or use `wiki_trace` to
go from a feature or rule to its code, or from a file to the features it serves. Both return
`citations` with `documentId` and `passage`: they are where to read next.

The wiki is written by a model. Use it to know where to look and quote only the sources. If
`pendingSources` or `stalePages` is above zero in `list_wikis`, or the page is `stale`, the code may
have changed since the page was written: verify every fact.

**3. Sources.** Choose by kind of question.

- **Code**: how something is built or computed.
  - Call `outline_document` (`documentId`, or `path`) on the one to three files the wiki pointed
    to. It lists declarations and constants with their `line` and `passage`.
  - Then call `read_passages` with `from` set to that passage, or with `line`.
  - Use `grep_passages` only with a precise identifier (class, function, constant, route,
    message), narrowed with `path` or the repository's `folderId`. Matches with `definition: true`
    are declarations and come first. A broad word returns hundreds of passages and a `hint`.
  - Code has no embeddings. `search_knowledge_base` cannot see it, but it finds the documentation
    that describes it (README, design notes, agent skills).
- **Documents, rules and regulations**:
  - Call `grep_passages` with a short, distinctive `pattern`: a term, an acronym, a reference
    number or an exact phrase. Try both an acronym and its expanded form. Set `regex: true` for
    alternatives (`SAVS|SAMSAH`) or variable spacing (`L\. ?311-4`). `^` and `$` match at line
    ends.
  - Call `search_knowledge_base` for a question with no distinctive wording, or when grep finds
    nothing relevant.
  - Call `search_exhaustive` only for in-depth research, when the indexed search misses the
    subject or all its scores are close.

**4. Read before quoting.** Call `read_passages` with 2 to 4 passages around each result you use:
grep returns only an excerpt, and a single passage often cuts a list or a rule in half.

Pass `folderId` or `documentId` to the search tools when the user named a folder or document.

Report each passage with its document title and passage number, best match first. Cite code as
`path:line` and name the repository's synchronised commit, because the copy can be older than the
Git host. Quote verbatim and attribute; do not merge passages into claims they do not support. If
nothing relevant is found, say so.
