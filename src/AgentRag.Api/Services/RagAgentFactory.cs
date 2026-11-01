using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentRag.Api.Services;

public class RagAgentFactory(IChatClient chatClient)
{
    private const string Instructions =
        "You are a helpful assistant. Always call the search_knowledge_base tool to find relevant " +
        "information before answering, and answer using only that information. " +
        "If nothing relevant is found, say you don't know. Cite document titles you used.";

    public AIAgent Create(RagService rag, string userId)
    {
        [Description("Searches the user's uploaded documents for passages relevant to the query.")]
        async Task<string> SearchKnowledgeBase([Description("The search query")] string query)
        {
            var results = await rag.SearchAsync(userId, query);
            return results.Count == 0
                ? "No relevant documents found."
                : string.Join("\n---\n", results.Select(r => $"[{r.DocumentTitle}] {r.Content}"));
        }

        return chatClient.AsAIAgent(
            instructions: Instructions,
            name: "RagAgent",
            tools: [AIFunctionFactory.Create(SearchKnowledgeBase, "search_knowledge_base")]);
    }
}
