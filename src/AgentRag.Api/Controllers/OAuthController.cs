using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AgentRag.Api.Auth;
using AgentRag.Api.Data;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AgentRag.Api.Controllers;

public sealed class OAuthController(OAuthSettings settings, IOpenIddictApplicationManager applications,
    IOpenIddictAuthorizationManager authorizations, UserManager<ApplicationUser> users) : Controller
{
    [HttpGet("/.well-known/oauth-protected-resource")]
    [HttpGet("/.well-known/oauth-protected-resource/mcp")]
    public IActionResult ResourceMetadata() => Json(new
    {
        resource = settings.Resource, authorization_servers = new[] { new Uri(settings.Issuer).AbsoluteUri },
        scopes_supported = new[] { OAuthSettings.Scope }, bearer_methods_supported = new[] { "header" }
    });

    [HttpPost("/connect/register"), EnableRateLimiting("oauth-register"), RequestSizeLimit(16384)]
    public async Task<IActionResult> Register([FromBody] JsonElement metadata)
    {
        if (!TryClientMetadata(metadata, out var redirects, out var name, settings.AllowedClientRedirectUris))
            return BadRequest(new { error = "invalid_client_metadata", error_description = "Public clients require literal loopback callbacks or an explicitly allowed HTTPS callback." });
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            ClientType = ClientTypes.Public, DisplayName = name, ConsentType = ConsentTypes.Explicit
        };
        foreach (var redirect in redirects) descriptor.RedirectUris.Add(redirect);
        descriptor.Permissions.UnionWith([
            Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
            Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code, Permissions.Prefixes.Scope + OAuthSettings.Scope,
            Permissions.Prefixes.Resource + settings.Resource
        ]);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        await applications.CreateAsync(descriptor);
        Response.Headers.CacheControl = "no-store";
        return StatusCode(201, new
        {
            client_id = descriptor.ClientId, client_name = name, redirect_uris = redirects.Select(u => u.AbsoluteUri),
            token_endpoint_auth_method = "none", grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" }
        });
    }

    internal static bool TryClientMetadata(JsonElement metadata, out List<Uri> redirects, out string name,
        IReadOnlyCollection<string>? allowedClientRedirectUris = null)
    {
        redirects = [];
        name = "MCP client";
        if (metadata.ValueKind != JsonValueKind.Object) return false;
        if (metadata.TryGetProperty("client_name", out var n))
        {
            if (n.ValueKind != JsonValueKind.String || n.GetString() is not { Length: > 0 and <= 100 } text) return false;
            name = text;
        }
        if (metadata.TryGetProperty("token_endpoint_auth_method", out var method) && (method.ValueKind != JsonValueKind.String || method.GetString() != "none")) return false;
        if (metadata.TryGetProperty("grant_types", out var grants) &&
            (grants.ValueKind != JsonValueKind.Array || grants.EnumerateArray().Any(g => g.ValueKind != JsonValueKind.String || g.GetString() is not ("authorization_code" or "refresh_token")))) return false;
        if (metadata.TryGetProperty("response_types", out var responses) &&
            (responses.ValueKind != JsonValueKind.Array || responses.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String || r.GetString() != "code"))) return false;
        if (!metadata.TryGetProperty("redirect_uris", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 10) return false;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
            var nativeCallback = uri.Scheme == "http" && uri.Host is "127.0.0.1" or "[::1]";
            var hostedCallback = uri.Scheme == "https" && allowedClientRedirectUris?.Contains(uri.AbsoluteUri, StringComparer.Ordinal) == true;
            if (!nativeCallback && !hostedCallback) return false;
            redirects.Add(uri);
        }
        return true;
    }

    [HttpGet("/connect/authorize")]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest()!;
        if (!ValidResource(request) || request.CodeChallengeMethod != CodeChallengeMethods.Sha256 || !request.HasScope(OAuthSettings.Scope))
            return ProtocolError(Errors.InvalidRequest, "The MCP resource, agentrag.read scope and S256 PKCE are required.");
        var cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!cookie.Succeeded)
            return Redirect("/account/login?returnUrl=" + Uri.EscapeDataString(Request.Path + Request.QueryString));
        var application = await applications.FindByClientIdAsync(request.ClientId!) ?? throw new InvalidOperationException();
        ViewData["ClientName"] = await applications.GetDisplayNameAsync(application);
        ViewData["Email"] = cookie.Principal!.Identity!.Name;
        return View("Consent");
    }

    [HttpPost("/connect/authorize"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Consent([FromForm] string decision)
    {
        var request = HttpContext.GetOpenIddictServerRequest()!;
        if (!ValidResource(request) || request.CodeChallengeMethod != CodeChallengeMethods.Sha256 || !request.HasScope(OAuthSettings.Scope))
            return ProtocolError(Errors.InvalidRequest, "Invalid authorization request.");
        var cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var user = cookie.Succeeded ? await users.GetUserAsync(cookie.Principal!) : null;
        if (user is null) return Unauthorized();
        if (decision != "allow") return ProtocolError(Errors.AccessDenied, "Authorization declined.");
        var identity = new ClaimsIdentity("OAuth", Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id);
        identity.SetClaim(ClaimTypes.NameIdentifier, user.Id);
        identity.SetClaim("security_stamp", await users.GetSecurityStampAsync(user));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetResources(settings.Resource);
        principal.SetDestinations(c => c.Type is Claims.Subject or ClaimTypes.NameIdentifier ? [Destinations.AccessToken] : []);
        var app = await applications.FindByClientIdAsync(request.ClientId!) ?? throw new InvalidOperationException();
        var authorization = await authorizations.CreateAsync(principal, user.Id,
            (await applications.GetIdAsync(app))!, AuthorizationTypes.Permanent, principal.GetScopes());
        principal.SetAuthorizationId(await authorizations.GetIdAsync(authorization));
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("/connect/token")]
    public async Task<IActionResult> Token()
    {
        var request = HttpContext.GetOpenIddictServerRequest()!;
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            return ProtocolError(Errors.UnsupportedGrantType, "Unsupported grant.");
        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null) return ProtocolError(Errors.InvalidGrant, "Invalid token.");
        var principal = result.Principal;
        var user = await users.FindByIdAsync(principal.GetClaim(Claims.Subject)!);
        if (user is null || await users.IsLockedOutAsync(user) ||
            principal.GetClaim("security_stamp") != await users.GetSecurityStampAsync(user))
            return ProtocolError(Errors.InvalidGrant, "The account authorization is no longer valid.");
        if (request.GetResources().Any() && !ValidResource(request))
            return ProtocolError(Errors.InvalidTarget, "Unknown MCP resource.");
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private bool ValidResource(OpenIddictRequest request) => request.GetResources().SequenceEqual([settings.Resource]);

    private ForbidResult ProtocolError(string error, string description) => Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
    }), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
}
