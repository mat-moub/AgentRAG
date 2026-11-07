using System.Net;
using System.Security.Cryptography.X509Certificates;
using AgentRag.Api.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AgentRag.Api.Auth;

public static class OAuthExtensions
{
    public const string McpScheme = "AgentRag.Mcp";

    public static IServiceCollection AddAgentRagOAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment environment,
        Action<OpenIddictServerBuilder>? configureTestKeys = null)
    {
        var settings = config.GetSection("OAuth").Get<OAuthSettings>() ?? new();
        settings.Validate(environment.IsDevelopment());
        services.AddSingleton(settings);
        services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
        services.Configure<IdentityOptions>(o =>
        {
            o.User.RequireUniqueEmail = true;
            o.Lockout.MaxFailedAccessAttempts = 5;
        });
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "AgentRag.Session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        services.AddAuthentication().AddPolicyScheme(McpScheme, null, o =>
        {
            // Identity tokens are opaque Data Protection tickets; OAuth tokens are JWT/JWE.
            o.ForwardDefaultSelector = c => c.Request.Headers.Authorization.ToString().Count(ch => ch == '.') >= 2
                ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme : IdentityConstants.BearerScheme;
        });
        if (!string.IsNullOrWhiteSpace(config["Authentication:Google:ClientId"]))
            services.AddAuthentication().AddGoogle(o =>
            {
                o.ClientId = config["Authentication:Google:ClientId"]!;
                o.ClientSecret = config["Authentication:Google:ClientSecret"]
                    ?? throw new InvalidOperationException("Google ClientSecret is required.");
                o.SignInScheme = IdentityConstants.ExternalScheme;
                o.Scope.Add("openid");
                o.SaveTokens = false;
                o.Events.OnRemoteFailure = c =>
                {
                    c.HandleResponse();
                    c.Response.Redirect("/account/login?error=google_failed");
                    return Task.CompletedTask;
                };
            });
        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
            .AddServer(o =>
            {
                o.SetIssuer(new Uri(settings.Issuer));
                o.SetAuthorizationEndpointUris("connect/authorize").SetTokenEndpointUris("connect/token")
                    .SetRevocationEndpointUris("connect/revoke");
                o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
                o.RegisterScopes(OAuthSettings.Scope, Scopes.OfflineAccess);
                o.RegisterResources(settings.Resource);
                o.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ApplyConfigurationResponseContext>(handler =>
                    handler.UseInlineHandler(context =>
                    {
                        context.Response["registration_endpoint"] = settings.Issuer.TrimEnd('/') + "/connect/register";
                        return default;
                    }));
                o.SetAccessTokenLifetime(TimeSpan.FromHours(1)).SetRefreshTokenLifetime(TimeSpan.FromDays(30));
                // Default rolling refresh tokens remain enabled, with no replay grace period.
                o.SetRefreshTokenReuseLeeway(TimeSpan.Zero);
                if (configureTestKeys is not null)
                {
                    if (!environment.IsDevelopment()) throw new InvalidOperationException("Test keys require Development.");
                    configureTestKeys(o);
                    o.UseAspNetCore().DisableTransportSecurityRequirement();
                }
                else if (environment.IsDevelopment())
                {
                    o.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
                    o.UseAspNetCore().DisableTransportSecurityRequirement();
                }
                else
                {
                    o.AddSigningCertificate(LoadCertificate(config, "Signing"));
                    o.AddEncryptionCertificate(LoadCertificate(config, "Encryption"));
                }
                o.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.AddAudiences(settings.Resource);
                o.EnableTokenEntryValidation();
                o.EnableAuthorizationEntryValidation();
                o.UseAspNetCore();
            });
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in config.GetSection("OAuth:KnownProxies").Get<string[]>() ?? [])
                o.KnownProxies.Add(IPAddress.Parse(proxy));
        });
        services.AddRateLimiter(o => o.AddFixedWindowLimiter("oauth-register", l =>
        {
            l.PermitLimit = 10;
            l.Window = TimeSpan.FromMinutes(1);
            l.QueueLimit = 0;
        }));
        return services;
    }

    private static X509Certificate2 LoadCertificate(IConfiguration config, string kind) =>
        X509CertificateLoader.LoadPkcs12FromFile(config[$"OAuth:{kind}CertificatePath"]
            ?? throw new InvalidOperationException($"OAuth {kind} certificate is required in production."),
            config[$"OAuth:{kind}CertificatePassword"]);

    public static void UseAgentRagForwardedHeaders(this WebApplication app) => app.UseForwardedHeaders();

    public static void UseAgentRagCookieProtection(this WebApplication app)
    {
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/account") || context.Request.Path.StartsWithSegments("/connect"))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Frame-Options"] = "DENY";
            }
            if (context.Request.Path.StartsWithSegments("/account") || context.Request.Path == "/signin-google")
            {
                var origin = new Uri(context.RequestServices.GetRequiredService<OAuthSettings>().Issuer);
                if (!string.Equals(context.Request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(context.Request.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Open this account page using the configured OAuth:Issuer origin.");
                    return;
                }
            }
            context.Response.OnStarting(() =>
            {
                if (context.Request.Path == "/mcp" && context.Response.StatusCode == 401)
                    context.Response.Headers.WWWAuthenticate = "Bearer resource_metadata=\"" +
                        context.RequestServices.GetRequiredService<OAuthSettings>().Issuer.TrimEnd('/') +
                        "/.well-known/oauth-protected-resource\"";
                return Task.CompletedTask;
            });
            if (context.Request.Path.StartsWithSegments("/api") &&
                !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
                !HttpMethods.IsOptions(context.Request.Method))
            {
                var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                var bearer = await context.AuthenticateAsync(IdentityConstants.BearerScheme);
                if (cookie.Succeeded && !bearer.Succeeded)
                {
                    // This middleware also guards remote callbacks before authentication runs.
                    // Antiforgery must validate against the same cookie identity that issued the token.
                    context.User = cookie.Principal!;
                    try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                    catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; return; }
                }
            }
            await next(context);
        });
    }
}
