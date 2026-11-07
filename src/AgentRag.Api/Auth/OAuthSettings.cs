namespace AgentRag.Api.Auth;

public sealed class OAuthSettings
{
    public string Issuer { get; set; } = "http://127.0.0.1:5000/";
    public string SiteUrl { get; set; } = "http://127.0.0.1:5173/";
    public string Resource => Issuer.TrimEnd('/') + "/mcp";
    public const string Scope = "agentrag.read";

    public void Validate(bool development)
    {
        foreach (var value in new[] { Issuer, SiteUrl })
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
                uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" ||
                (uri.Scheme != "https" && !(development && uri.Scheme == "http" && uri.IsLoopback)))
                throw new InvalidOperationException("OAuth URLs must be HTTPS origins; HTTP loopback is allowed only in Development.");
    }
}
