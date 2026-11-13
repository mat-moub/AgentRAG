namespace AgentRag.Api.Auth;

public sealed class OAuthSettings
{
    public string Issuer { get; set; } = "http://127.0.0.1:5000/";
    public string SiteUrl { get; set; } = "http://127.0.0.1:5173/";
    // OpenAI's stable callback is used when RFC 9207 issuer identification is supported.
    // Additional hosted clients must be explicitly registered here, never by a host wildcard.
    // Claude custom connectors (claude.ai, desktop and mobile apps) call back on claude.ai; claude.com is its announced successor.
    public string[] AllowedClientRedirectUris { get; set; } =
    [
        "https://chatgpt.com/connector_platform_oauth_redirect",
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback"
    ];
    public string Resource => Issuer.TrimEnd('/') + "/mcp";
    public const string Scope = "agentrag.read";

    public void Validate(bool development)
    {
        foreach (var value in new[] { Issuer, SiteUrl })
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
                uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" ||
                (uri.Scheme != "https" && !(development && uri.Scheme == "http" && uri.IsLoopback)))
                throw new InvalidOperationException("OAuth URLs must be HTTPS origins; HTTP loopback is allowed only in Development.");
        foreach (var value in AllowedClientRedirectUris)
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                value.Contains('*') || value != uri.AbsoluteUri)
                throw new InvalidOperationException("Hosted OAuth callbacks must be exact absolute HTTPS URLs without credentials, query, fragment or wildcards.");
    }
}
