using System.Security.Claims;
using AgentRag.Api.Auth;
using AgentRag.Api.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AgentRag.Api.Controllers;

public sealed class AccountController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
    OAuthSettings settings, IAuthenticationSchemeProvider schemes, IAntiforgery antiforgery) : Controller
{
    [HttpGet("/api/auth/session")]
    public async Task<IActionResult> Session()
    {
        Response.Headers.CacheControl = "no-store";
        var cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var user = cookie.Succeeded ? await users.GetUserAsync(cookie.Principal!) : null;
        return Json(new
        {
            authenticated = user is not null, email = user?.Email,
            googleEnabled = await GoogleEnabledAsync(),
            googleLinked = user is not null && (await users.GetLoginsAsync(user)).Any(l => l.LoginProvider == "Google"),
            csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken
        });
    }

    [HttpPost("/api/auth/logout")]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return NoContent();
    }

    [HttpGet("/account/login")]
    public async Task<IActionResult> Login(string? returnUrl, string? error)
    {
        Prepare(returnUrl);
        ViewData["GoogleEnabled"] = await GoogleEnabledAsync();
        if (error is not null) ViewData["Error"] = "Google sign-in was cancelled or failed. Please try again.";
        return View();
    }

    [HttpPost("/account/login"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Login([FromForm] string email, [FromForm] string password, [FromForm] string? returnUrl)
    {
        var user = await CheckPasswordAsync(email, password);
        if (user is null)
        {
            await Login(returnUrl, null);
            ViewData["Error"] = "Unable to sign in. Check your credentials. Accounts requiring two-factor authentication cannot use this form.";
            return View();
        }
        await signIn.SignInAsync(user, false);
        return Finish(returnUrl);
    }

    [HttpGet("/account/google/start")]
    public async Task<IActionResult> GoogleStart(string? returnUrl)
    {
        if (!await GoogleEnabledAsync()) return Redirect("/account/login?error=google_disabled");
        var callback = Url.Action(nameof(GoogleCallback), new { returnUrl = SafeReturn(returnUrl) })!;
        return Challenge(signIn.ConfigureExternalAuthenticationProperties("Google", callback), "Google");
    }

    [HttpGet("/account/link")]
    public IActionResult Link()
    {
        Prepare(null);
        return View();
    }

    [HttpPost("/account/link"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Link([FromForm] string email, [FromForm] string password)
    {
        if (!await GoogleEnabledAsync()) return BadRequest("Google sign-in is not configured.");
        var user = await CheckPasswordAsync(email, password);
        if (user is null)
        {
            ViewData["Error"] = "Check your AgentRAG credentials. Two-factor accounts cannot use this form.";
            return View();
        }
        var properties = signIn.ConfigureExternalAuthenticationProperties("Google", Url.Action(nameof(GoogleCallback))!, user.Id);
        properties.Items["link_user"] = user.Id;
        properties.Items["link_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        return Challenge(properties, "Google");
    }

    [HttpGet("/account/google/callback")]
    public async Task<IActionResult> GoogleCallback(string? returnUrl)
    {
        var external = await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
        var linkId = external.Properties is { } props && props.Items.TryGetValue("link_user", out var id) ? id : null;
        var info = await signIn.GetExternalLoginInfoAsync(linkId);
        if (info is null) return Redirect("/account/login?error=google_failed");
        if (linkId is not null)
        {
            var time = external.Properties!.Items.TryGetValue("link_time", out var t) ? t : null;
            if (!long.TryParse(time, out var seconds) ||
                DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds) > TimeSpan.FromMinutes(5))
                return BadRequest("Please confirm your password again before linking Google.");
            var target = await users.FindByIdAsync(linkId);
            if (target is null || await users.IsLockedOutAsync(target)) return Unauthorized();
            var linked = await users.AddLoginAsync(target, info);
            if (!linked.Succeeded) return LinkFailure();
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            await signIn.SignInAsync(target, false);
            return Finish(null);
        }
        var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (user is null)
            return Redirect("/account/google/new?returnUrl=" + Uri.EscapeDataString(SafeReturn(returnUrl)));
        if (!await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user) || await users.GetTwoFactorEnabledAsync(user))
            return Unauthorized("This account cannot sign in using this flow.");
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        await signIn.SignInAsync(user, false);
        return Finish(returnUrl);
    }

    [HttpGet("/account/google/new")]
    public async Task<IActionResult> GoogleNew(string? returnUrl)
    {
        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null) return Redirect("/account/login");
        Prepare(returnUrl);
        ViewData["GoogleEmail"] = info.Principal.FindFirstValue(ClaimTypes.Email);
        return View("GoogleNew");
    }

    [HttpPost("/account/google/new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> GoogleNew([FromForm] string decision, [FromForm] string? email,
        [FromForm] string? password, [FromForm] string? returnUrl)
    {
        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null) return Unauthorized();
        ApplicationUser? user;
        if (decision == "link") user = await CheckPasswordAsync(email ?? "", password ?? "");
        else if (decision == "create")
        {
            var googleEmail = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrWhiteSpace(googleEmail) || await users.FindByEmailAsync(googleEmail) is not null)
                return await NewFailureAsync(returnUrl, "An account already uses this email. Sign in below to associate it.");
            user = new ApplicationUser { UserName = googleEmail, Email = googleEmail, DisplayName = info.Principal.Identity?.Name };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded) return await NewFailureAsync(returnUrl, "The account could not be created. Please sign in to your existing account.");
        }
        else return BadRequest();
        if (user is null) return await NewFailureAsync(returnUrl, "Check your AgentRAG credentials. Two-factor accounts cannot use this form.");
        var linked = await users.AddLoginAsync(user, info);
        if (!linked.Succeeded) return LinkFailure();
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        await signIn.SignInAsync(user, false);
        return Finish(returnUrl);
    }

    private async Task<ApplicationUser?> CheckPasswordAsync(string email, string password)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null || await users.GetTwoFactorEnabledAsync(user)) return null;
        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        return result.Succeeded ? user : null;
    }

    private IActionResult LinkFailure() => BadRequest("This Google account could not be associated. It may already belong to another account.");
    private async Task<IActionResult> NewFailureAsync(string? returnUrl, string error)
    {
        await GoogleNew(returnUrl);
        ViewData["Error"] = error;
        return View("GoogleNew");
    }
    private Task<bool> GoogleEnabledAsync() => IsGoogleEnabledAsync(schemes);
    private static async Task<bool> IsGoogleEnabledAsync(IAuthenticationSchemeProvider provider) =>
        await provider.GetSchemeAsync(GoogleDefaults.AuthenticationScheme) is not null;
    private static string SafeReturn(string? value) => value is not null && value.StartsWith("/connect/authorize?", StringComparison.Ordinal) ? value : "/";
    private void Prepare(string? returnUrl) => ViewData["ReturnUrl"] = SafeReturn(returnUrl);
    private IActionResult Finish(string? returnUrl) => SafeReturn(returnUrl) == "/" ? Redirect(settings.SiteUrl) : LocalRedirect(SafeReturn(returnUrl));
}
