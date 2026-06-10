using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using GZCTF.Middlewares;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Internal;
using GZCTF.Utils;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GZCTF.Controllers;

/// <summary>
/// Unified external OAuth (Google / Discord) sign-in gateway. A single redirect URI —
/// <c>/api/oauth/callback</c> — is shared by every provider (the provider is recovered from
/// the OAuth <c>state</c>), so only one URL is registered per provider console. The flow is
/// hand-rolled (authorize redirect → code exchange → userinfo → create/link/sign-in) rather
/// than using ASP.NET's per-provider handler callbacks, which cannot share a callback path.
/// CSRF is enforced by binding <c>state</c> to a one-time cache entry AND an httponly cookie.
/// </summary>
public partial class AccountController
{
    private const string GoogleProvider = "Google";
    private const string DiscordProvider = "Discord";
    private const string OAuthStateCookie = "GZCTF_OAuthState";
    private const string OAuthStateCachePrefix = "_OAuthState_";
    private const string OAuthCallbackPath = "/api/oauth/callback";

    private sealed record OAuthEndpoints(string Authorize, string Token, string UserInfo, string Scope);

    private sealed record OAuthStateData(string Provider, string ReturnUrl);

    private static readonly Dictionary<string, OAuthEndpoints> Providers = new()
    {
        [GoogleProvider] = new OAuthEndpoints(
            "https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token",
            "https://openidconnect.googleapis.com/v1/userinfo",
            "openid email profile"),
        [DiscordProvider] = new OAuthEndpoints(
            "https://discord.com/api/oauth2/authorize",
            "https://discord.com/api/oauth2/token",
            "https://discord.com/api/users/@me",
            "identify email"),
    };

    /// <summary>
    /// Begin an external OAuth sign-in
    /// </summary>
    /// <remarks>
    /// Full-page redirect entry point — redirects the browser to the provider's consent
    /// screen. Only works for a provider that is configured (client id + secret present).
    /// </remarks>
    /// <param name="oauthConfig"></param>
    /// <param name="provider">OAuth provider: <c>google</c> or <c>discord</c></param>
    /// <param name="returnUrl">Local URL to return to after sign-in (defaults to <c>/</c>)</param>
    /// <param name="token"></param>
    /// <response code="302">Redirect to the provider's authorization endpoint</response>
    /// <response code="400">Unknown or disabled provider</response>
    [HttpGet("/api/oauth/{provider}")]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Register))]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> OAuthStart(
        [FromServices] IOptionsSnapshot<OAuthConfig> oauthConfig,
        [FromRoute] string provider,
        [FromQuery] string? returnUrl = null,
        CancellationToken token = default)
    {
        var scheme = NormalizeProvider(provider);
        if (scheme is null || !TryGetClient(oauthConfig.Value, scheme, out var clientId, out _))
            return BadRequest(new RequestResponse("The requested OAuth provider is not available.",
                StatusCodes.Status400BadRequest));

        var safeReturn = returnUrl is not null && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        // One-time, browser-bound CSRF token: the callback requires this exact state in both
        // the cache (proves we issued it) and the cookie (proves it's the same browser).
        await cache.SetStringAsync($"{OAuthStateCachePrefix}{state}",
            JsonSerializer.Serialize(new OAuthStateData(scheme, safeReturn)),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) },
            token);

        Response.Cookies.Append(OAuthStateCookie, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax, // sent on the top-level GET redirect back from the provider
            Path = "/api/oauth",
            MaxAge = TimeSpan.FromMinutes(10)
        });

        var ep = Providers[scheme];
        var authorizeUrl = QueryHelpers.AddQueryString(ep.Authorize, new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = OAuthCallbackUrl(),
            ["response_type"] = "code",
            ["scope"] = ep.Scope,
            ["state"] = state,
        });
        return Redirect(authorizeUrl);
    }

    /// <summary>
    /// Unified OAuth callback
    /// </summary>
    /// <remarks>
    /// Single redirect URI for all providers. Validates state (cache + cookie), exchanges the
    /// authorization code for an access token, reads the userinfo, then signs in an existing
    /// linked account, auto-links to an existing account by provider-verified email, or creates
    /// a new account (subject to the registration policy + email-domain whitelist). On any
    /// failure it redirects to <c>/account/login?error=oauth_*</c>.
    /// </remarks>
    [HttpGet(OAuthCallbackPath)]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Register))]
    public async Task<IActionResult> OAuthCallback(
        [FromServices] IOptionsSnapshot<OAuthConfig> oauthConfig,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] AppDbContext dbContext,
        [FromQuery] string? code = null,
        [FromQuery] string? state = null,
        [FromQuery] string? error = null,
        CancellationToken token = default)
    {
        if (!string.IsNullOrEmpty(error))
            return OAuthError("provider_error");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return OAuthError("no_info");

        // CSRF: the state must match the browser cookie AND a live one-time cache entry.
        var cookieState = Request.Cookies[OAuthStateCookie];
        Response.Cookies.Delete(OAuthStateCookie, new CookieOptions { Path = "/api/oauth" });
        if (string.IsNullOrEmpty(cookieState)
            || !CryptographicOperations.FixedTimeEquals(cookieState.ToUTF8Bytes(), state.ToUTF8Bytes()))
            return OAuthError("invalid_state");

        var cacheKey = $"{OAuthStateCachePrefix}{state}";
        var stateJson = await cache.GetStringAsync(cacheKey, token);
        await cache.RemoveAsync(cacheKey, token); // one-time use
        if (string.IsNullOrEmpty(stateJson))
            return OAuthError("invalid_state");

        OAuthStateData? stateData;
        try { stateData = JsonSerializer.Deserialize<OAuthStateData>(stateJson); }
        catch { return OAuthError("invalid_state"); }
        if (stateData is null || !Providers.ContainsKey(stateData.Provider))
            return OAuthError("invalid_state");

        var scheme = stateData.Provider;
        if (!TryGetClient(oauthConfig.Value, scheme, out var clientId, out var clientSecret))
            return OAuthError("provider_error");

        var ep = Providers[scheme];
        var http = httpClientFactory.CreateClient();

        // Exchange the authorization code for an access token.
        string? accessToken;
        try
        {
            using var tokenResp = await http.PostAsync(ep.Token, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = OAuthCallbackUrl(),
                }), token);
            var body = await tokenResp.Content.ReadAsStringAsync(token);
            if (!tokenResp.IsSuccessStatusCode)
            {
                logger.SystemLog($"OAuth token exchange failed ({scheme}): {(int)tokenResp.StatusCode} "
                    + body[..Math.Min(body.Length, 300)], TaskStatus.Failed, LogLevel.Warning);
                return OAuthError("token_failed");
            }
            using var doc = JsonDocument.Parse(body);
            accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        }
        catch (Exception e)
        {
            logger.LogError(e, "OAuth token exchange error for {Provider}", scheme);
            return OAuthError("token_failed");
        }

        if (string.IsNullOrEmpty(accessToken))
            return OAuthError("token_failed");

        // Fetch the user profile.
        JsonElement userInfo;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ep.UserInfo);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var uResp = await http.SendAsync(req, token);
            if (!uResp.IsSuccessStatusCode)
                return OAuthError("userinfo_failed");
            var uBody = await uResp.Content.ReadAsStringAsync(token);
            using var uDoc = JsonDocument.Parse(uBody);
            userInfo = uDoc.RootElement.Clone();
        }
        catch (Exception e)
        {
            logger.LogError(e, "OAuth userinfo error for {Provider}", scheme);
            return OAuthError("userinfo_failed");
        }

        var (providerKey, email, emailVerified, name, avatarUrl) = ParseUserInfo(scheme, userInfo);
        if (string.IsNullOrEmpty(providerKey))
            return OAuthError("no_info");

        return await HandleExternalIdentityAsync(scheme, providerKey, email, emailVerified, name, avatarUrl,
            http, dbContext, stateData.ReturnUrl, token);
    }

    /// <summary>
    /// Create/link/sign-in for a resolved external identity. Shared by all providers.
    /// </summary>
    private async Task<IActionResult> HandleExternalIdentityAsync(
        string provider, string providerKey, string? email, bool emailVerified, string? name, string? avatarUrl,
        HttpClient http, AppDbContext dbContext, string safeReturn, CancellationToken token)
    {
        // Admin-approval mode: ActiveOnRegister=false AND email confirmation is NOT the gate,
        // so EmailConfirmed doubles as the manual "approved" flag an admin flips.
        var requiresManualApproval =
            !accountPolicy.Value.ActiveOnRegister && !accountPolicy.Value.EmailConfirmationRequired;

        // 1) Already linked → sign in (subject to the approval gate).
        var user = await userManager.FindByLoginAsync(provider, providerKey);
        if (user is not null)
        {
            if (user.Role == Role.Banned)
                return OAuthError("account_disabled");
            if (!await signInManager.CanSignInAsync(user))
                return OAuthError("await_approval");
            if (await CheckAntiCheatConflictAsync(user, null, dbContext, token) is not null)
                return OAuthError("anti_cheat");
            await TrySetOAuthAvatarAsync(user, avatarUrl, http, token);
            await CompleteExternalSignInAsync(user, provider);
            return LocalRedirect(safeReturn);
        }

        // Linking or creating requires a provider-verified email (guards against spoofing).
        if (string.IsNullOrWhiteSpace(email))
            return OAuthError("no_email");
        if (!emailVerified)
            return OAuthError("email_unverified");

        // 2) A CONFIRMED account with this email exists → auto-link. Requiring EmailConfirmed
        // is the takeover guard: an account is only safe to merge into if its owner already
        // proved control of this inbox. An unconfirmed account is rejected so a pre-registered
        // "squat" on someone else's email (created via password register but never verified)
        // can't be hijacked into via OAuth.
        user = await userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            if (user.Role == Role.Banned)
                return OAuthError("account_disabled");
            if (!user.EmailConfirmed)
                return OAuthError("email_conflict");

            var link = await userManager.AddLoginAsync(user, new UserLoginInfo(provider, providerKey, provider));
            if (!link.Succeeded)
                return OAuthError("link_failed");

            if (!await signInManager.CanSignInAsync(user))
                return OAuthError("await_approval");
            if (await CheckAntiCheatConflictAsync(user, null, dbContext, token) is not null)
                return OAuthError("anti_cheat");
            await TrySetOAuthAvatarAsync(user, avatarUrl, http, token);
            await CompleteExternalSignInAsync(user, provider);
            return LocalRedirect(safeReturn);
        }

        // 3) New account, honouring the registration policy.
        if (!accountPolicy.Value.AllowRegister)
            return OAuthError("register_disabled");
        if (!VerifyEmailDomain(email))
            return OAuthError("email_domain");

        var newUser = new UserInfo
        {
            UserName = await GenerateUniqueUserNameAsync(name, email),
            Email = email,
            EmailConfirmed = !requiresManualApproval, // provider-verified above
            Role = environment.IsDevelopment() ? Role.Admin : Role.User
        };
        newUser.UpdateByHttpContext(HttpContext);

        var created = await userManager.CreateAsync(newUser);
        if (!created.Succeeded)
            return OAuthError("create_failed");

        var linked = await userManager.AddLoginAsync(newUser, new UserLoginInfo(provider, providerKey, provider));
        if (!linked.Succeeded)
            return OAuthError("link_failed");

        if (requiresManualApproval)
        {
            logger.Log($"New OAuth account pending admin approval via {provider}",
                newUser.UserName ?? "Anonymous", newUser.IP?.ToString(), TaskStatus.Pending);
            return OAuthError("await_approval");
        }

        if (await CheckAntiCheatConflictAsync(newUser, null, dbContext, token) is not null)
            return OAuthError("anti_cheat");

        await TrySetOAuthAvatarAsync(newUser, avatarUrl, http, token);
        await CompleteExternalSignInAsync(newUser, provider);
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

        // Clear any prior/partial auth ticket first (matches the password LogIn flow), then
        // issue the application cookie (GZCTF_Token). No browser-fingerprint claim: the OAuth
        // redirect flow collects none.
        await signInManager.SignOutAsync();
        await signInManager.SignInWithClaimsAsync(user, true, []);

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
    /// Best-effort: import the provider's profile picture as the user's avatar. Only sets one
    /// when the user has none (never overwrites a chosen avatar). The image goes through the
    /// same resize/normalise path as a manual upload. The caller persists the user afterwards.
    /// </summary>
    private async Task TrySetOAuthAvatarAsync(UserInfo user, string? avatarUrl, HttpClient http, CancellationToken token)
    {
        if (string.IsNullOrEmpty(avatarUrl) || user.AvatarHash is not null)
            return;

        // SSRF guard: only fetch https URLs from the providers' known CDNs.
        if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        var host = uri.Host;
        var allowed = host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
            return;

        try
        {
            using var resp = await http.GetAsync(uri, token);
            if (!resp.IsSuccessStatusCode)
                return;
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return;
            var bytes = await resp.Content.ReadAsByteArrayAsync(token);
            if (bytes.Length is 0 or > 5 * 1024 * 1024)
                return;

            using var ms = new MemoryStream(bytes);
            var file = new FormFile(ms, 0, bytes.Length, "avatar", "avatar")
            {
                Headers = new HeaderDictionary(),
                ContentType = contentType
            };
            var avatar = await blobService.CreateOrUpdateImage(file, "avatar", 300, token);
            if (avatar is not null)
                user.AvatarHash = avatar.Hash;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to import OAuth avatar for {User}", user.UserName);
        }
    }

    /// <summary>Public callback URL registered with every provider (single, shared).</summary>
    private string OAuthCallbackUrl() => $"{Request.Scheme}://{Request.Host}{OAuthCallbackPath}";

    /// <summary>Map a user-supplied provider name to the canonical provider key.</summary>
    private static string? NormalizeProvider(string? provider) => provider?.ToLowerInvariant() switch
    {
        "google" => GoogleProvider,
        "discord" => DiscordProvider,
        _ => null
    };

    /// <summary>
    /// Resolve a provider's client id + (decoded) secret from config. Returns false unless both
    /// are present (i.e. the provider is configured/enabled).
    /// </summary>
    private bool TryGetClient(OAuthConfig config, string scheme, out string clientId, out string clientSecret)
    {
        clientId = string.Empty;
        clientSecret = string.Empty;
        var (id, secret) = scheme switch
        {
            GoogleProvider => (config.GoogleClientId, config.GoogleClientSecret),
            DiscordProvider => (config.DiscordClientId, config.DiscordClientSecret),
            _ => (null, null)
        };
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            return false;
        clientId = id;
        clientSecret = DecodeSecret(secret);
        return !string.IsNullOrEmpty(clientSecret);
    }

    /// <summary>Decode an XOR+base64 client secret (falls back to raw when no XorKey is set).</summary>
    private string DecodeSecret(string stored)
    {
        var xorKey = configService.GetXorKey();
        if (xorKey.Length == 0)
            return stored;
        try
        {
            return System.Text.Encoding.UTF8.GetString(Codec.Xor(Convert.FromBase64String(stored), xorKey));
        }
        catch
        {
            return stored;
        }
    }

    /// <summary>Extract (providerKey, email, emailVerified, displayName, avatarUrl) from userinfo JSON.</summary>
    private static (string key, string? email, bool emailVerified, string? name, string? avatarUrl) ParseUserInfo(
        string scheme, JsonElement u)
    {
        string? Str(string p) =>
            u.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string p) =>
            u.TryGetProperty(p, out var v)
            && (v.ValueKind == JsonValueKind.True
                || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

        switch (scheme)
        {
            case GoogleProvider:
                return (Str("sub") ?? string.Empty, Str("email"), Bool("email_verified"), Str("name"), Str("picture"));
            case DiscordProvider:
                var id = Str("id") ?? string.Empty;
                var avatarHash = Str("avatar");
                var avatarUrl = !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(avatarHash)
                    ? $"https://cdn.discordapp.com/avatars/{id}/{avatarHash}.png?size=256"
                    : null;
                return (id, Str("email"), Bool("verified"), Str("global_name") ?? Str("username"), avatarUrl);
            default:
                return (string.Empty, null, false, null, null);
        }
    }

    /// <summary>
    /// Derive a unique username from the provider display name (falling back to the email
    /// local-part), appending a random suffix on collision.
    /// </summary>
    private async Task<string> GenerateUniqueUserNameAsync(string? name, string email)
    {
        var baseName = name;
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
