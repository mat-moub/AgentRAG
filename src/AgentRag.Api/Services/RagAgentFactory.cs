using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentRag.Api.Services;

public class RagAgentFactory(IChatClient chatClient)
{
    private const string Instructions =
        "You are a helpful assistant. Use the conversation history to understand follow-up questions " +
        "and remember details the user has shared, such as their name. You may answer greetings and " +
        "questions about the conversation directly from that history. " +
        "For questions about documents or external facts, always call the search_knowledge_base tool " +
        "to find relevant information before answering, and ground the answer in the retrieved passages. " +
        "Previous assistant messages are not independent evidence of document facts. " +
        "If neither the conversation nor the retrieved passages provide the answer, say you don't know. " +
        "Cite document titles only when using retrieved document information.";

    public AIAgent Create(RagService rag, string userId, CancellationToken cancellationToken = default)
    {
        [Description("Searches the user's uploaded documents for passages relevant to the query.")]
        async Task<string> SearchKnowledgeBase([Description("The search query")] string query)
        {
            var results = await rag.SearchAsync(userId, query, ct: cancellationToken);
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
