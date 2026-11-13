---
description: Draft a user story from a need, grounded in your AgentRAG product documentation and connected code repositories. Use when a product owner wants a story, acceptance criteria or a functional note that matches what the product and code actually do.
argument-hint: <need or feature idea> [folderId]
allowed-tools: mcp__plugin_agentrag_agentrag__grep_passages, mcp__plugin_agentrag_agentrag__search_knowledge_base, mcp__plugin_agentrag_agentrag__search_exhaustive, mcp__plugin_agentrag_agentrag__read_passages, mcp__plugin_agentrag_agentrag__list_documents, mcp__plugin_agentrag_agentrag__list_repositories
---

Draft a user story for `$ARGUMENTS` from the user's AgentRAG library. Write for a product
owner: plain language, no code in the story itself.

1. **What the product says.** Search the product documentation for the feature and its
   vocabulary: `grep_passages` on the key terms first, then `search_knowledge_base`, then
   `search_exhaustive` if needed. Read the best matches with `read_passages`.
2. **What the code does.** Call `list_repositories`. If a repository is connected, grep it
   (pass its `folderId`) for the terms found in step 1 and their likely code names: screens,
   routes, entities, error messages, permissions. Read around the matches to learn the current
   behaviour, the rules already enforced and what the change would touch. Code has no
   embeddings, so use grep only.
3. **Write the story** in the user's language:
   - title;
   - "As a … I want … so that …";
   - context: current behaviour, with what the documentation says and what the code does, and
     any difference between the two;
   - acceptance criteria as testable Given / When / Then statements;
   - out of scope, and open questions for anything the sources do not settle.
4. **Sources.** List the document passages (title and passage number) and code locations
   (`path:line`, with the repository's synchronised commit) each statement relies on.

Do not invent behaviour. Where neither the documentation nor the code establishes something,
put it under open questions rather than in the acceptance criteria. If no repository is
connected, say the story is based on documentation only.
