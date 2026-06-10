using System.Security.Claims;
using System.Security.Cryptography;
using GZCTF.Middlewares;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Utils;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GZCTF.Controllers;

/// <summary>
/// External OAuth (Google / Discord) sign-in. The flow is a full-page browser redirect:
/// <c>ExternalLogin</c> issues a provider challenge → provider consent → the provider's
/// handler middleware processes its callback (<c>/signin-google</c> / <c>/signin-discord</c>)
/// and signs into the external scheme → <c>ExternalCallback</c> creates/links the account,
/// issues the application cookie, and redirects back into the SPA. State/PKCE/code-exchange
/// are handled entirely by ASP.NET Identity + the provider handlers.
/// </summary>
public partial class AccountController
{
    private const string GoogleProvider = "Google";
    private const string DiscordProvider = "Discord";

    /// <summary>
    /// Begin an external OAuth sign-in
    /// </summary>
    /// <remarks>
    /// Full-page redirect entry point. Redirects the browser to the provider's consent
    /// screen. Only works for a provider that is configured (client id + secret present).
    /// </remarks>
    /// <param name="oauthConfig"></param>
    /// <param name="provider">OAuth provider: <c>Google</c> or <c>Discord</c></param>
    /// <param name="returnUrl">Local URL to return to after sign-in (defaults to <c>/</c>)</param>
    /// <response code="302">Redirect to the provider's authorization endpoint</response>
    /// <response code="400">Unknown or disabled provider</response>
    [HttpGet("/api/oauth/{provider}")]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Register))]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public IActionResult ExternalLogin(
        [FromServices] IOptionsSnapshot<Models.Internal.OAuthConfig> oauthConfig,
        [FromRoute] string provider,
        [FromQuery] string? returnUrl = null)
    {
        var scheme = NormalizeProvider(provider);
        var enabled = scheme switch
        {
            GoogleProvider => oauthConfig.Value.GoogleEnabled,
            DiscordProvider => oauthConfig.Value.DiscordEnabled,
            _ => false
        };

        if (scheme is null || !enabled)
            return BadRequest(new RequestResponse("The requested OAuth provider is not available.",
                StatusCodes.Status400BadRequest));

        var safeReturn = returnUrl is not null && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        var redirectUrl = Url.Action(nameof(ExternalCallback), "Account", new { returnUrl = safeReturn });
        var properties = signInManager.ConfigureExternalAuthenticationProperties(scheme, redirectUrl);
        return Challenge(properties, scheme);
    }

    /// <summary>
    /// External OAuth callback
    /// </summary>
    /// <remarks>
    /// The provider handler redirects here after a successful consent. Signs in an existing
    /// linked account, auto-links to an existing account by provider-verified email, or
    /// creates a new account (subject to the registration policy + email-domain whitelist).
    /// On any failure it redirects to <c>/account/login?error=oauth_*</c>; on success it
    /// redirects to the requested local return URL.
    /// </remarks>
    /// <param name="dbContext"></param>
    /// <param name="returnUrl">Local URL to return to after sign-in</param>
    /// <param name="remoteError">Error reported by the provider, if any</param>
    /// <param name="token"></param>
    /// <response code="302">Redirect into the SPA (success or error)</response>
    [HttpGet("/api/oauth/callback")]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Register))]
    public async Task<IActionResult> ExternalCallback(
        [FromServices] AppDbContext dbContext,
        [FromQuery] string? returnUrl = null,
        [FromQuery] string? remoteError = null,
        CancellationToken token = default)
    {
        var safeReturn = returnUrl is not null && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";

        if (remoteError is not null)
            return OAuthError("provider_error");

        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
            return OAuthError("no_info");

        // Admin-approval mode: ActiveOnRegister=false AND email confirmation is NOT the gate,
        // so EmailConfirmed doubles as the manual "approved" flag an admin flips. In that mode
        // a provider-verified email must NOT auto-confirm/sign-in an account.
        var requiresManualApproval =
            !accountPolicy.Value.ActiveOnRegister && !accountPolicy.Value.EmailConfirmationRequired;

        // 1) The external identity is already linked → sign in (subject to the approval gate).
        var user = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (user is not null)
        {
            if (user.Role == Role.Banned)
                return OAuthError("account_disabled");
            // CanSignInAsync enforces RequireConfirmedEmail — blocks a still-pending account
            // (SignInAsync itself would not), closing the admin-approval bypass.
            if (!await signInManager.CanSignInAsync(user))
                return OAuthError("await_approval");
            if (await CheckAntiCheatConflictAsync(user, null, dbContext, token) is not null)
                return OAuthError("anti_cheat");
            await CompleteExternalSignInAsync(user, info.LoginProvider);
            return LocalRedirect(safeReturn);
        }

        // Linking or creating requires a provider-verified email (operator policy:
        // auto-link only when the provider verified the address — guards against spoofing).
        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
            return OAuthError("no_email");
        if (!IsExternalEmailVerified(info))
            return OAuthError("email_unverified");

        // 2) An account with this (verified) email exists → auto-link the external login.
        user = await userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            if (user.Role == Role.Banned)
                return OAuthError("account_disabled");

            var link = await userManager.AddLoginAsync(user, info);
            if (!link.Succeeded)
                return OAuthError("link_failed");

            // The provider just verified the address — confirm a pending email-confirmation
            // account. But in admin-approval mode, EmailConfirmed is the approval flag, so
            // leave it for the admin and block sign-in below.
            if (!user.EmailConfirmed && !requiresManualApproval)
            {
                user.EmailConfirmed = true;
                await userManager.UpdateAsync(user);
            }

            if (!await signInManager.CanSignInAsync(user))
                return OAuthError("await_approval");
            if (await CheckAntiCheatConflictAsync(user, null, dbContext, token) is not null)
                return OAuthError("anti_cheat");
            await CompleteExternalSignInAsync(user, info.LoginProvider);
            return LocalRedirect(safeReturn);
        }

        // 3) No account yet → create one, honouring the registration policy.
        if (!accountPolicy.Value.AllowRegister)
            return OAuthError("register_disabled");
        if (!VerifyEmailDomain(email))
            return OAuthError("email_domain");

        var newUser = new UserInfo
        {
            UserName = await GenerateUniqueUserNameAsync(info, email),
            Email = email,
            // Provider-verified email satisfies email confirmation. In admin-approval mode the
            // account stays pending (EmailConfirmed=false) until an admin approves it.
            EmailConfirmed = !requiresManualApproval,
            Role = environment.IsDevelopment() ? Role.Admin : Role.User
        };
        newUser.UpdateByHttpContext(HttpContext);

        var created = await userManager.CreateAsync(newUser);
        if (!created.Succeeded)
            return OAuthError("create_failed");

        var linked = await userManager.AddLoginAsync(newUser, info);
        if (!linked.Succeeded)
            return OAuthError("link_failed");

        // Admin-approval mode: created pending, do NOT sign in — mirrors the password Register
        // flow's AdminConfirmationRequired outcome.
        if (requiresManualApproval)
        {
            logger.Log($"New OAuth account pending admin approval via {info.LoginProvider}",
                newUser.UserName ?? "Anonymous", newUser.IP?.ToString(), TaskStatus.Pending);
            return OAuthError("await_approval");
        }

        if (await CheckAntiCheatConflictAsync(newUser, null, dbContext, token) is not null)
            return OAuthError("anti_cheat");

        await CompleteExternalSignInAsync(newUser, info.LoginProvider);
        return LocalRedirect(safeReturn);
    }

    /// <summary>
    /// Issue the application cookie for an externally-authenticated user.
    /// </summary>
    private async Task CompleteExternalSignInAsync(UserInfo user, string provider)
    {
        user.LastSignedInUtc = DateTimeOffset.UtcNow;
        user.UpdateByHttpContext(HttpContext);
        await userManager.UpdateAsync(user);

        // SignInAsync issues the application cookie (GZCTF_Token). The temporary external
        // cookie is no longer needed. No browser-fingerprint claim: the OAuth redirect flow
        // collects none.
        await signInManager.SignInAsync(user, isPersistent: true);

        logger.Log($"User signed in via {provider}",
            user.UserName ?? "Anonymous", user.IP?.ToString(), TaskStatus.Success);
    }

    /// <summary>
    /// Shared IP / browser-fingerprint anti-cheat gate used by both password login and the
    /// external-OAuth callback. Returns a non-null 403 result (and records an AntiCheatBlock)
    /// when the configured uniqueness policy is violated, or null when sign-in may proceed.
    /// For OAuth there is no browser fingerprint, so pass <paramref name="fingerprint"/> null
    /// — only the IP checks then apply.
    /// </summary>
    private async Task<IActionResult?> CheckAntiCheatConflictAsync(
        UserInfo user, string? fingerprint, AppDbContext dbContext, CancellationToken token)
    {
        var policy = accountPolicy.Value;
        // IP and fingerprint uniqueness each have a per-team flag (conflict only with a
        // teammate) and a global flag (conflict with ANY other user in the last 24h).
        // The "global" variants block a login if a *different* account already used the
        // same IP / fingerprint, regardless of team — for events where every player must
        // connect from a distinct machine/address. The candidate set below is widened to
        // all recent users whenever either global flag is on; each individual check then
        // matches against teammates-only or everyone per its own flags.
        var ipCheck = policy.RequireUniqueIpPerTeamUser || policy.RequireUniqueIpGlobal;
        var fpCheck = policy.RequireUniqueFingerprintPerTeamUser || policy.RequireUniqueFingerprintGlobal;
        if (!ipCheck && !fpCheck)
            return null;

        var currentIp = HttpContext.Connection.RemoteIpAddress;
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var anyGlobal = policy.RequireUniqueIpGlobal || policy.RequireUniqueFingerprintGlobal;
        var candidates = await userManager.Users
            .Where(u => u.Id != user.Id
                && u.LastVisitedUtc > since
                && (anyGlobal || u.Teams.Any(t => t.Members.Any(m => m.Id == user.Id))))
            .Select(u => new
            {
                u.Id, u.UserName, u.IP, u.BrowserFingerprint,
                IsTeammate = u.Teams.Any(t => t.Members.Any(m => m.Id == user.Id))
            })
            .ToListAsync(token);

        if (ipCheck && currentIp is not null)
        {
            // Global → any user with this IP; per-team only → restrict to teammates.
            var conflict = candidates.FirstOrDefault(t =>
                t.IP is not null && t.IP.Equals(currentIp)
                && (policy.RequireUniqueIpGlobal || t.IsTeammate));
            if (conflict is not null)
            {
                logger.Log(
                    StaticLocalizer[nameof(Resources.Program.Account_TeammateIpInUse), conflict.UserName ?? "?"],
                    user.UserName ?? "Anonymous",
                    currentIp.ToString(),
                    TaskStatus.Failed);
                dbContext.AntiCheatBlocks.Add(new AntiCheatBlock
                {
                    UserId = user.Id,
                    UserName = user.UserName,
                    ConflictUserId = conflict.Id,
                    ConflictUserName = conflict.UserName,
                    Kind = AntiCheatBlockKind.Ip,
                    ConflictingValue = currentIp.ToString()
                });
                await dbContext.SaveChangesAsync(token);
                return new ObjectResult(new RequestResponse(
                    localizer[nameof(Resources.Program.Account_TeammateIpInUse), conflict.UserName ?? "?"],
                    StatusCodes.Status403Forbidden))
                { StatusCode = StatusCodes.Status403Forbidden };
            }
        }

        if (fpCheck && !string.IsNullOrEmpty(fingerprint))
        {
            var conflict = candidates.FirstOrDefault(t =>
                t.BrowserFingerprint == fingerprint
                && (policy.RequireUniqueFingerprintGlobal || t.IsTeammate));
            if (conflict is not null)
            {
                logger.Log(
                    StaticLocalizer[nameof(Resources.Program.Account_TeammateFingerprintInUse), conflict.UserName ?? "?"],
                    user.UserName ?? "Anonymous",
                    currentIp?.ToString(),
                    TaskStatus.Failed);
                dbContext.AntiCheatBlocks.Add(new AntiCheatBlock
                {
                    UserId = user.Id,
                    UserName = user.UserName,
                    ConflictUserId = conflict.Id,
                    ConflictUserName = conflict.UserName,
                    Kind = AntiCheatBlockKind.Fingerprint,
                    ConflictingValue = fingerprint
                });
                await dbContext.SaveChangesAsync(token);
                return new ObjectResult(new RequestResponse(
                    localizer[nameof(Resources.Program.Account_TeammateFingerprintInUse), conflict.UserName ?? "?"],
                    StatusCodes.Status403Forbidden))
                { StatusCode = StatusCodes.Status403Forbidden };
            }
        }

        return null;
    }

    /// <summary>
    /// Map a user-supplied provider name to the registered authentication scheme.
    /// </summary>
    private static string? NormalizeProvider(string? provider) => provider?.ToLowerInvariant() switch
    {
        "google" => GoogleProvider,
        "discord" => DiscordProvider,
        _ => null
    };

    /// <summary>
    /// Whether the external provider reported the email address as verified. Set during the
    /// provider handler's OnCreatingTicket (see IdentityExtension) into a single claim.
    /// </summary>
    private static bool IsExternalEmailVerified(ExternalLoginInfo info) =>
        string.Equals(info.Principal.FindFirstValue(ContextHelper.ExternalEmailVerifiedClaimType),
            "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derive a unique username from the provider display name (falling back to the email
    /// local-part), appending a random suffix on collision.
    /// </summary>
    private async Task<string> GenerateUniqueUserNameAsync(ExternalLoginInfo info, string email)
    {
        var baseName = info.Principal.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = email.Split('@')[0];
        baseName = baseName.Trim();
        if (baseName.Length > 24)
            baseName = baseName[..24];
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "user";

        if (await userManager.FindByNameAsync(baseName) is null)
            return baseName;

        for (var i = 0; i < 50; i++)
        {
            var candidate = $"{baseName}_{RandomNumberGenerator.GetInt32(1000, 10000)}";
            if (await userManager.FindByNameAsync(candidate) is null)
                return candidate;
        }

        return $"{baseName}_{Guid.CreateVersion7():N}";
    }

    /// <summary>
    /// Redirect to the SPA login page with a namespaced error code the client localizes.
    /// </summary>
    private IActionResult OAuthError(string code) =>
        Redirect($"/account/login?error=oauth_{Uri.EscapeDataString(code)}");
}
