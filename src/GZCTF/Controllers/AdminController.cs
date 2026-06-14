using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GZCTF.Extensions;
using GZCTF.Middlewares;
using GZCTF.Models.Internal;
using GZCTF.Models.Request.Account;
using GZCTF.Models.Request.Admin;
using GZCTF.Models.Request.Info;
using GZCTF.Models.Request.Game;
using GZCTF.Models.Response.Admin;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using GZCTF.Services.Config;
using GZCTF.Services.Mail;
using GZCTF.Storage.Interface;
using GZCTF.Utils;
using GZCTF.Models.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace GZCTF.Controllers;

/// <summary>
/// Administration APIs
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status403Forbidden)]
public class AdminController(
    UserManager<UserInfo> userManager,
    ILogger<AdminController> logger,
    IBlobStorage storage,
    CacheHelper cacheHelper,
    IBlobRepository blobService,
    ILogRepository logRepository,
    IConfigService configService,
    IGameRepository gameRepository,
    ITeamRepository teamRepository,
    IContainerRepository containerRepository,
    IServiceProvider serviceProvider,
    IParticipationRepository participationRepository,

    IChallengeReviewRepository challengeReviewRepository,
    ICheatInfoRepository cheatInfoRepository,
    IStringLocalizer<Program> localizer) : ControllerBase
{
    /// <summary>
    /// Get configuration
    /// </summary>
    /// <remarks>
    /// Use this API to get global settings, requires Admin permission
    /// </remarks>
    /// <response code="200">Global configuration</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpGet("Config")]
    [ProducesResponseType(typeof(ConfigEditModel), StatusCodes.Status200OK)]
    public IActionResult GetConfigs()
    {
        // always reload, ensure latest
        configService.ReloadConfig();

        // For sections that carry secrets, blank the obfuscated bytes
        // before returning. The UI doesn't need them — it shows a
        // "(configured)" placeholder via HasPassword/HasSecretKey and
        // only sends a value back when the operator intentionally
        // types one.
        // Safe copies blank the obfuscated secrets for transport but
        // explicitly populate the HasX surrogates from the source —
        // the computed fallback on the safe copy would always return
        // false (its own Password is now empty), so the UI couldn't
        // tell "no password set" from "password set, just hidden".
        var buildRegistry = serviceProvider.GetRequiredService<IOptionsSnapshot<BuildRegistryConfig>>().Value;
        var safeBuildRegistry = new BuildRegistryConfig
        {
            PushOnBuild = buildRegistry.PushOnBuild,
            Server = buildRegistry.Server,
            Namespace = buildRegistry.Namespace,
            Username = buildRegistry.Username,
            Password = buildRegistry.HasPassword ? string.Empty : null,
            HasPassword = buildRegistry.HasPassword,
        };

        var email = serviceProvider.GetRequiredService<IOptionsSnapshot<EmailConfig>>().Value;
        var safeEmail = new EmailConfig
        {
            UserName = email.UserName,
            Password = email.HasPassword ? string.Empty : string.Empty,
            SenderAddress = email.SenderAddress,
            SenderName = email.SenderName,
            Smtp = email.Smtp is null ? null : new SmtpConfig
            {
                Host = email.Smtp.Host,
                Port = email.Smtp.Port,
                BypassCertVerify = email.Smtp.BypassCertVerify,
            },
            HasPassword = email.HasPassword,
        };

        var captcha = serviceProvider.GetRequiredService<IOptionsSnapshot<CaptchaConfig>>().Value;
        var safeCaptcha = new CaptchaConfig
        {
            Provider = captcha.Provider,
            SiteKey = captcha.SiteKey,
            SecretKey = captcha.HasSecretKey ? string.Empty : null,
            HashPow = captcha.HashPow,
            HasSecretKey = captcha.HasSecretKey,
        };

        var registry = serviceProvider.GetRequiredService<IOptionsSnapshot<RegistryConfig>>().Value;
        var safeRegistry = new RegistryConfig
        {
            ServerAddress = registry.ServerAddress,
            UserName = registry.UserName,
            Password = registry.HasPassword ? string.Empty : null,
            HasPassword = registry.HasPassword,
        };

        var oauth = serviceProvider.GetRequiredService<IOptionsSnapshot<OAuthConfig>>().Value;
        var safeOAuth = new OAuthConfig
        {
            GoogleClientId = oauth.GoogleClientId,
            GoogleClientSecret = oauth.HasGoogleClientSecret ? string.Empty : null,
            DiscordClientId = oauth.DiscordClientId,
            DiscordClientSecret = oauth.HasDiscordClientSecret ? string.Empty : null,
            HasGoogleClientSecret = oauth.HasGoogleClientSecret,
            HasDiscordClientSecret = oauth.HasDiscordClientSecret,
        };

        var containerProvider = serviceProvider.GetRequiredService<IOptionsSnapshot<ContainerProvider>>().Value;
        var isK8s = containerProvider.Type == ContainerProviderType.Kubernetes;

        ConfigEditModel config = new()
        {
            AccountPolicy = serviceProvider.GetRequiredService<IOptionsSnapshot<AccountPolicy>>().Value,
            GlobalConfig = serviceProvider.GetRequiredService<IOptionsSnapshot<GlobalConfig>>().Value,
            ContainerPolicy = serviceProvider.GetRequiredService<IOptionsSnapshot<ContainerPolicy>>().Value,
            BuildRegistry = safeBuildRegistry,
            Email = safeEmail,
            Captcha = safeCaptcha,
            OAuth = safeOAuth,
            Registry = safeRegistry,
            ProxyTrust = serviceProvider.GetRequiredService<IOptionsSnapshot<ProxyTrustConfig>>().Value,
            ContainerProvider = new ContainerProviderInfoModel
            {
                Type = containerProvider.Type,
                PortMappingType = containerProvider.PortMappingType,
                TrafficCapture = containerProvider.EnableTrafficCapture,
                KubernetesNamespace = isK8s ? containerProvider.KubernetesConfig?.Namespace : null,
                ImagePullPolicy = isK8s ? containerProvider.KubernetesConfig?.ImagePullPolicy : null,
            },
        };

        return Ok(config);
    }

    /// <summary>
    /// Change configuration
    /// </summary>
    /// <remarks>
    /// Use this API to change global settings, requires Admin permission
    /// </remarks>
    /// <response code="200">Update successful</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpPut("Config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateConfigs([FromBody] ConfigEditModel model, CancellationToken token)
    {
        // handle api encryption config
        var global = serviceProvider.GetRequiredService<IOptionsSnapshot<GlobalConfig>>().Value;
        if (!global.ApiEncryption && model.GlobalConfig?.ApiEncryption is true)
            await configService.UpdateApiEncryptionKey(token);

        // Special-case the build-registry password: arrives plaintext
        // from the form, gets XOR-obfuscated before persistence. Empty
        // string means "leave the existing password alone" — the UI
        // shows a "(configured)" placeholder when one is stored and
        // only sends a value when the operator types one.
        if (model.BuildRegistry is { } br)
        {
            if (string.IsNullOrEmpty(br.Password))
            {
                var existing = serviceProvider.GetRequiredService<IOptionsSnapshot<BuildRegistryConfig>>().Value;
                br.Password = existing.Password;
            }
            else
            {
                var xorKey = configService.GetXorKey();
                if (xorKey.Length > 0)
                    br.Password = Convert.ToBase64String(
                        Codec.Xor(br.Password.ToUTF8Bytes(), xorKey));
                // If no XorKey is configured (test envs), the password
                // lands plaintext in the DB. That's the same risk
                // profile as the existing RegistryConfig.Password
                // handling, which never obfuscated either — call out
                // in the operator docs.
            }
        }

        // EmailConfig — same preserve-on-blank + XOR pattern as
        // BuildRegistry. When the operator leaves the SMTP password
        // field empty in /admin/settings, keep the stored value.
        if (model.Email is { } emailModel)
        {
            if (string.IsNullOrEmpty(emailModel.Password))
            {
                var existing = serviceProvider.GetRequiredService<IOptionsSnapshot<EmailConfig>>().Value;
                emailModel.Password = existing.Password;
            }
            else
            {
                var xorKey = configService.GetXorKey();
                if (xorKey.Length > 0)
                    emailModel.Password = Convert.ToBase64String(
                        Codec.Xor(emailModel.Password.ToUTF8Bytes(), xorKey));
            }
        }

        // CaptchaConfig — same shape, but the secret is `SecretKey`.
        // SiteKey is public and saved as-is.
        if (model.Captcha is { } captchaModel)
        {
            if (string.IsNullOrEmpty(captchaModel.SecretKey))
            {
                var existing = serviceProvider.GetRequiredService<IOptionsSnapshot<CaptchaConfig>>().Value;
                captchaModel.SecretKey = existing.SecretKey;
            }
            else
            {
                var xorKey = configService.GetXorKey();
                if (xorKey.Length > 0)
                    captchaModel.SecretKey = Convert.ToBase64String(
                        Codec.Xor(captchaModel.SecretKey.ToUTF8Bytes(), xorKey));
            }
        }

        // RegistryConfig (private-image pull credentials).
        if (model.Registry is { } registryModel)
        {
            if (string.IsNullOrEmpty(registryModel.Password))
            {
                var existing = serviceProvider.GetRequiredService<IOptionsSnapshot<RegistryConfig>>().Value;
                registryModel.Password = existing.Password;
            }
            else
            {
                var xorKey = configService.GetXorKey();
                if (xorKey.Length > 0)
                    registryModel.Password = Convert.ToBase64String(
                        Codec.Xor(registryModel.Password.ToUTF8Bytes(), xorKey));
            }
        }

        // OAuthConfig — two secrets (Google + Discord), same preserve-on-blank + XOR
        // pattern. Client ids are public and saved as-is.
        if (model.OAuth is { } oauthModel)
        {
            var existing = serviceProvider.GetRequiredService<IOptionsSnapshot<OAuthConfig>>().Value;
            var xorKey = configService.GetXorKey();

            string? ObfuscateOrKeep(string? incoming, string? stored) =>
                string.IsNullOrEmpty(incoming)
                    ? stored // empty from the form means "leave the stored secret alone"
                    : xorKey.Length > 0
                        ? Convert.ToBase64String(Codec.Xor(incoming.ToUTF8Bytes(), xorKey))
                        : incoming;

            oauthModel.GoogleClientSecret = ObfuscateOrKeep(oauthModel.GoogleClientSecret, existing.GoogleClientSecret);
            oauthModel.DiscordClientSecret = ObfuscateOrKeep(oauthModel.DiscordClientSecret, existing.DiscordClientSecret);
        }

        // save all config properties
        foreach (var prop in typeof(ConfigEditModel).GetProperties())
        {
            var value = prop.GetValue(model);

            if (value is null)
                continue;

            await configService.SaveConfig(prop.PropertyType, value, token);
        }

        return Ok();
    }

    /// <summary>
    /// Send a test email to verify SMTP configuration
    /// </summary>
    /// <remarks>
    /// Drives the "Send test" button in /admin/settings → Email
    /// (SMTP). Uses the supplied config (operator-typed, possibly
    /// unsaved) to send a single plain-text message. Nothing is
    /// persisted. When the request body's password field is empty,
    /// the stored DB password is decrypted and used — matching the
    /// preserve-on-blank shape of the Save flow so the operator can
    /// test without re-typing a configured password.
    /// </remarks>
    /// <response code="200">Test email accepted by the SMTP server</response>
    /// <response code="400">SMTP rejected the message; error text in the response body</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpPost("Email/Test")]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Concurrency))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TestEmail([FromBody] EmailTestModel model, CancellationToken token)
    {
        if (!ModelState.IsValid)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Model_ValidationFailed)]));

        var mailSender = serviceProvider.GetRequiredService<IMailSender>();

        // Resolve the plaintext SMTP password. If the operator left
        // the password field blank in the form, the GetConfigs path
        // had already blanked it for transport — fall back to the
        // stored DB value and XOR-decrypt it. Otherwise treat the
        // posted value as fresh plaintext from the operator.
        var passwordPlain = model.Config.Password;
        if (string.IsNullOrEmpty(passwordPlain))
        {
            var stored = serviceProvider.GetRequiredService<IOptionsSnapshot<EmailConfig>>().Value;
            var xorKey = configService.GetXorKey();
            passwordPlain = DecryptStoredPassword(stored.Password, xorKey);
        }

        var (ok, err) = await mailSender.TestSendAsync(model.Config, passwordPlain, model.Recipient, token);
        return ok
            ? Ok()
            : BadRequest(new RequestResponse(
                localizer[nameof(Resources.Program.Admin_EmailTestFailed), err ?? string.Empty]));
    }

    /// <summary>Reverse the XOR + base64 obfuscation written by
    /// <see cref="UpdateConfigs"/> for password fields. Mirror of the
    /// helper inside <c>MailSender.DecryptPassword</c> — kept inline
    /// here so the test endpoint doesn't have to round-trip through
    /// the singleton just to read its stored password.</summary>
    private static string DecryptStoredPassword(string? stored, byte[] xorKey)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (xorKey.Length == 0) return stored;
        try
        {
            return Encoding.UTF8.GetString(
                Codec.Xor(Convert.FromBase64String(stored), xorKey));
        }
        catch
        {
            return stored;
        }
    }

    /// <summary>
    /// Verify captcha configuration
    /// </summary>
    /// <remarks>
    /// Drives the "Test" button on /admin/settings → Captcha. For
    /// Turnstile, probes Cloudflare's siteverify endpoint with the
    /// configured SecretKey + a deliberately-bogus response token —
    /// Cloudflare returns 'invalid-input-response' when the secret
    /// itself is valid (success), or 'invalid-input-secret' /
    /// 'missing-input-secret' when the secret is bad (failure). For
    /// HashPow, returns 200 if the difficulty is in the supported
    /// range (no remote service to probe). For 'None', returns 400.
    /// Preserve-on-blank for SecretKey mirrors the Save flow.
    /// </remarks>
    /// <response code="200">Captcha config looks valid</response>
    /// <response code="400">Captcha rejected the configuration; error text in the response body</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpPost("Captcha/Test")]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Concurrency))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TestCaptcha([FromBody] CaptchaTestModel model, CancellationToken token)
    {
        if (!ModelState.IsValid)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Model_ValidationFailed)]));

        switch (model.Config.Provider)
        {
            case CaptchaProvider.CloudflareTurnstile:
                return await TestTurnstile(model.Config, token);
            case CaptchaProvider.HashPow:
                // Server-side puzzle solving is the only validation;
                // the difficulty is the only knob and HashPowConfig
                // already clamps it to [8, 48] in its property
                // getter, so the worst the operator can do is set a
                // value outside that range, which silently snaps to
                // the bound on read. Nothing to fail.
                return Ok();
            case CaptchaProvider.None:
            default:
                return BadRequest(new RequestResponse(
                    localizer[nameof(Resources.Program.Admin_CaptchaTestFailed),
                        "captcha is disabled — nothing to test"]));
        }
    }

    private async Task<IActionResult> TestTurnstile(CaptchaConfig config, CancellationToken token)
    {
        // Resolve the plaintext SecretKey. Empty form field → fall
        // back to the stored DB value and XOR-decrypt. Non-empty →
        // treat as the operator's typed plaintext.
        var secretPlain = config.SecretKey;
        if (string.IsNullOrEmpty(secretPlain))
        {
            var stored = serviceProvider.GetRequiredService<IOptionsSnapshot<CaptchaConfig>>().Value;
            secretPlain = DecryptStoredPassword(stored.SecretKey, configService.GetXorKey());
        }

        if (string.IsNullOrWhiteSpace(secretPlain))
            return BadRequest(new RequestResponse(
                localizer[nameof(Resources.Program.Admin_CaptchaTestFailed), "SecretKey is required"]));

        // Cloudflare siteverify: post the secret with a deliberately
        // invalid response token. The error codes are how we tell a
        // bad secret from a good secret + bad token.
        // https://developers.cloudflare.com/turnstile/get-started/server-side-validation/#error-codes
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var req = new TurnstileRequestModel
        {
            Secret = secretPlain,
            Response = "test-token-from-admin-settings",
            RemoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty
        };

        try
        {
            var resp = await http.PostAsJsonAsync(
                "https://challenges.cloudflare.com/turnstile/v0/siteverify", req, token);
            var body = await resp.Content.ReadFromJsonAsync<TurnstileResponseModel>(token);

            if (body is null)
                return BadRequest(new RequestResponse(
                    localizer[nameof(Resources.Program.Admin_CaptchaTestFailed),
                        "Cloudflare returned no body"]));

            // Real success is impossible here (we sent a bogus token).
            // What we want is: success=false AND the error code points
            // at the response, not the secret.
            var bad = body.ErrorCodes
                .Any(c => c is "invalid-input-secret" or "missing-input-secret" or "bad-request");
            if (bad)
                return BadRequest(new RequestResponse(
                    localizer[nameof(Resources.Program.Admin_CaptchaTestFailed),
                        $"Cloudflare rejected SecretKey: {string.Join(", ", body.ErrorCodes)}"]));

            return Ok();
        }
        catch (Exception ex)
        {
            return BadRequest(new RequestResponse(
                localizer[nameof(Resources.Program.Admin_CaptchaTestFailed), ex.Message]));
        }
    }

    /// <summary>
    /// Diagnose how gzctf is detecting the caller's IP
    /// </summary>
    /// <remarks>
    /// Drives the "Check my IP" button on /admin/settings →
    /// Diagnostics. Helps an operator confirm the ForwardedHeaders
    /// middleware is configured correctly for their upstream proxy
    /// (traefik / k8s ingress / their own nginx) — if the detected
    /// IP comes back as the proxy's container IP, X-Forwarded-For
    /// isn't being honoured (usually a TrustedNetworks problem) and
    /// every team will appear to share one IP, breaking rate-limits
    /// and the "unique IP per team user" rule.
    /// </remarks>
    /// <response code="200">IP diagnostics</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpGet("MyIp")]
    [ProducesResponseType(typeof(MyIpInfoModel), StatusCodes.Status200OK)]
    public IActionResult MyIp()
    {
        // ForwardedHeaders middleware moves the original raw IP into
        // HttpContext.Connection.RemoteIpAddress (the "detected" IP)
        // ONLY when the connection came from a trusted proxy. The
        // pre-rewrite raw value is stashed in Items["X-Original-For"]
        // — present iff the middleware actually rewrote the address.
        var detected = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var originalRaw = HttpContext.Items["X-Original-For"]?.ToString();
        var headerValue = HttpContext.Request.Headers.TryGetValue("X-Forwarded-For", out var v)
            ? v.ToString() : string.Empty;

        // ForwardedOptions isn't registered as IOptions<> — only the
        // .NET ForwardedHeadersOptions is (populated from this wrapper
        // at startup via ToForwardedHeadersOptions). Read the wrapper
        // straight from IConfiguration the same way ServicesExtension
        // does so the operator sees what's actually configured.
        var cfg = serviceProvider.GetRequiredService<IConfiguration>();
        var fwd = cfg.GetSection(nameof(ForwardedOptions)).Get<ForwardedOptions>();
        var trusted = new List<string>();
        if (fwd?.TrustedNetworks is { } tn) trusted.AddRange(tn);
        if (fwd?.KnownIPNetworks is { } kn) trusted.AddRange(kn);
        if (fwd?.KnownNetworks is { } kn2) trusted.AddRange(kn2);

        return Ok(new MyIpInfoModel
        {
            DetectedIp = detected,
            RawConnectionIp = originalRaw ?? detected,
            ForwardedFor = headerValue,
            ProxyTrusted = originalRaw is not null,
            TrustedNetworks = trusted
        });
    }

    /// <summary>
    /// Change platform Logo
    /// </summary>
    /// <remarks>
    /// Use this API to change the platform Logo, requires Admin permission
    /// </remarks>
    /// <response code="200">Update successful</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpPost("Config/Logo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateLogo(IFormFile file, CancellationToken token)
    {
        switch (file.Length)
        {
            case 0:
                return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.File_SizeZero)]));
            case > 3 * 1024 * 1024:
                return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.File_SizeTooLarge)]));
        }

        if (!await DeleteCurrentLogo(token))
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Admin_LogoUpdateFailed)]));

        var logo = await blobService.CreateOrUpdateImage(file, "logo", 640, token);
        if (logo is null)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Admin_LogoUpdateFailed)]));

        var favicon = await blobService.CreateOrUpdateImage(file, "favicon", 256, token);
        if (favicon is null)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Admin_LogoUpdateFailed)]));

        HashSet<Config> configSet =
        [
            new($"{nameof(GlobalConfig)}:{nameof(GlobalConfig.LogoHash)}", logo.Hash, [CacheKey.ClientConfig]),
            new($"{nameof(GlobalConfig)}:{nameof(GlobalConfig.FaviconHash)}", favicon.Hash, [CacheKey.Favicon])
        ];

        await configService.SaveConfigSet(configSet, token);

        return Ok();
    }

    /// <summary>
    /// Reset platform Logo
    /// </summary>
    /// <remarks>
    /// Use this API to reset the platform Logo, requires Admin permission
    /// </remarks>
    /// <response code="200">Updated successfully</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpDelete("Config/Logo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetLogo(CancellationToken token)
    {
        if (!await DeleteCurrentLogo(token))
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Admin_LogoUpdateFailed)]));

        HashSet<Config> configSet =
        [
            new($"{nameof(GlobalConfig)}:{nameof(GlobalConfig.LogoHash)}", string.Empty, [CacheKey.ClientConfig]),
            new($"{nameof(GlobalConfig)}:{nameof(GlobalConfig.FaviconHash)}", string.Empty, [CacheKey.Favicon])
        ];

        await configService.SaveConfigSet(configSet, token);

        return Ok();
    }

    private async Task<bool> DeleteCurrentLogo(CancellationToken token)
    {
        var globalConfig = serviceProvider.GetRequiredService<IOptionsSnapshot<GlobalConfig>>().Value;

        return await DeleteByHash(globalConfig.LogoHash, token) &&
               await DeleteByHash(globalConfig.FaviconHash, token);
    }

    private async Task<bool> DeleteByHash(string? hash, CancellationToken token)
    {
        if (hash is not null && Codec.FileHashRegex().IsMatch(hash))
            return await blobService.DeleteBlobByHash(hash, token) switch
            {
                TaskStatus.Success or TaskStatus.NotFound => true,
                _ => false
            };

        return true;
    }

    /// <summary>
    /// Get all users
    /// </summary>
    /// <remarks>
    /// Use this API to get all users, requires Admin permission
    /// </remarks>
    /// <response code="200">User list</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpGet("Users")]
    [ProducesResponseType(typeof(ArrayResponse<UserInfoModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Users([FromQuery][Range(0, 500)] int count = 100, [FromQuery] int skip = 0,
        [FromQuery] string? search = null, CancellationToken token = default)
    {
        var query = userManager.Users.AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(u => u.UserName!.Contains(search) || u.Email!.Contains(search));

        return Ok((await query.OrderBy(e => e.Id).Skip(skip).Take(count)
                .Select(u => UserInfoModel.FromUserInfo(u))
                .ToArrayAsync(token))
            .ToResponse(await query.CountAsync(token)));
    }

    /// <summary>
    /// Add users in batch
    /// </summary>
    /// <remarks>
    /// Use this API to add users in batch, requires Admin permission
    /// </remarks>
    /// <response code="200">Successfully added</response>
    /// <response code="400">User validation failed</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpPost("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddUsers([FromBody] UserCreateModel[] model, CancellationToken token = default)
    {
        var currentUser = await userManager.GetUserAsync(User);
        var trans = await teamRepository.BeginTransactionAsync(token);

        try
        {
            var users = new List<(UserInfo, string?)>(model.Length);
            foreach (var user in model)
            {
                var userInfo = user.ToUserInfo();
                var result = await userManager.CreateAsync(userInfo, user.Password);

                if (result.Succeeded)
                {
                    users.Add((userInfo, user.TeamName));
                    continue;
                }

                userInfo = result.Errors.FirstOrDefault()?.Code switch
                {
                    "DuplicateEmail" => await userManager.FindByEmailAsync(user.Email),
                    "DuplicateUserName" => await userManager.FindByNameAsync(user.UserName),
                    _ => null
                };

                if (userInfo is null)
                {
                    await trans.RollbackAsync(token);
                    return HandleIdentityError(result.Errors);
                }

                userInfo.UpdateUserInfo(user);
                var code = await userManager.GeneratePasswordResetTokenAsync(userInfo);
                await userManager.ResetPasswordAsync(userInfo, code, user.Password);

                users.Add((userInfo, user.TeamName));
            }

            var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
            var teams = new List<Team>();
            foreach (var (user, teamName) in users)
            {
                if (teamName is null)
                    continue;

                // Reuse an existing team with this name — first from this import's
                // local list, then from the DB. Without the DB check a re-import of
                // the same roster created a SECOND "Team 01" etc. (team Name has no
                // unique index), duplicating every team. Idempotent now: existing
                // teams are joined, not recreated.
                var team = teams.Find(t => t.Name == teamName)
                           ?? await dbContext.Teams.Include(t => t.Members)
                               .FirstOrDefaultAsync(t => t.Name == teamName, token);
                if (team is null)
                {
                    team = await teamRepository.CreateTeam(new() { Name = teamName }, user, token);
                    teams.Add(team);
                }
                else
                {
                    if (team.Members.All(m => m.Id != user.Id))
                        team.Members.Add(user);
                    if (!teams.Contains(team))
                        teams.Add(team);
                }
            }

            await teamRepository.SaveAsync(token);
            await trans.CommitAsync(token);

            logger.Log(StaticLocalizer[nameof(Resources.Program.Admin_UserBatchAdded), users.Count],
                currentUser, TaskStatus.Success);

            return Ok();
        }
        catch
        {
            await trans.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>
    /// Import users from pre-parsed, user-reviewed rows
    /// </summary>
    /// <remarks>
    /// Accepts structured rows (after client-side CSV parsing and user editing).
    /// Auto-generates unique usernames and secure passwords server-side, creates accounts
    /// and teams in a single atomic transaction, and returns the full credentials list.
    /// Rate-limit-safe: one HTTP call regardless of import size.
    /// </remarks>
    /// <response code="200">Import complete — returns per-user credentials and summary counts</response>
    /// <response code="400">No rows provided or request is invalid</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpPost("Users/Import")]
    [ProducesResponseType(typeof(CsvImportResultModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportUsersFromCsv([FromBody] CsvImportRequest request, CancellationToken token = default)
    {
        if (request.Rows is null || request.Rows.Count == 0)
            return BadRequest(new RequestResponse("No rows provided"));

        string? NE(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        var takenUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toCreate = new List<(UserCreateModel Model, string RealName)>(request.Rows.Count);
        var skipped = new List<CsvImportUserResult>();

        foreach (var row in request.Rows)
        {
            var realName = row.RealName?.Trim() ?? string.Empty;
            var email = row.Email?.Trim().ToLowerInvariant() ?? string.Empty;

            if (string.IsNullOrEmpty(email) || !email.Contains('@'))
            {
                skipped.Add(new CsvImportUserResult { Email = email, RealName = realName, Status = "skipped", Error = "Invalid or missing email" });
                continue;
            }

            if (!seenEmails.Add(email))
            {
                skipped.Add(new CsvImportUserResult { Email = email, RealName = realName, Status = "skipped", Error = "Duplicate email" });
                continue;
            }

            // Username: use explicit override if provided, otherwise auto-generate from real name
            var username = !string.IsNullOrWhiteSpace(row.UserNameOverride)
                ? CsvEnsureUniqueUsername(row.UserNameOverride.Trim(), takenUsernames)
                : CsvGenerateUsername(realName, takenUsernames);

            string? teamName = request.TeamMode switch
            {
                "single" => NE(request.SingleTeamName),
                "fromrow" => NE(row.TeamName),
                _ => null
            };

            toCreate.Add((new UserCreateModel
            {
                UserName = username,
                Password = CsvGeneratePassword(),
                Email = email,
                RealName = NE(realName),
                StdNumber = NE(row.StdNumber),
                Phone = NE(row.Phone),
                TeamName = teamName,
            }, realName));
        }

        var currentUser = await userManager.GetUserAsync(User);
        var trans = await teamRepository.BeginTransactionAsync(token);
        var results = new List<CsvImportUserResult>(toCreate.Count);

        try
        {
            var created = new List<(UserInfo User, string? TeamName)>(toCreate.Count);

            foreach (var (model, realName) in toCreate)
            {
                var userInfo = model.ToUserInfo();
                userInfo.EmailConfirmed = request.EmailConfirmed;

                var result = await userManager.CreateAsync(userInfo, model.Password);
                string status = "created";

                if (!result.Succeeded)
                {
                    var errorCode = result.Errors.FirstOrDefault()?.Code;
                    userInfo = errorCode switch
                    {
                        "DuplicateEmail" => await userManager.FindByEmailAsync(model.Email),
                        "DuplicateUserName" => await userManager.FindByNameAsync(model.UserName),
                        _ => null
                    };

                    if (userInfo is null)
                    {
                        results.Add(new CsvImportUserResult
                        {
                            Email = model.Email, RealName = realName, UserName = model.UserName,
                            Status = "skipped", Error = result.Errors.FirstOrDefault()?.Description
                        });
                        continue;
                    }

                    userInfo.UpdateUserInfo(model);
                    var resetCode = await userManager.GeneratePasswordResetTokenAsync(userInfo);
                    await userManager.ResetPasswordAsync(userInfo, resetCode, model.Password);
                    status = "updated";
                }

                created.Add((userInfo, model.TeamName));
                results.Add(new CsvImportUserResult
                {
                    Email = model.Email,
                    RealName = realName,
                    UserName = userInfo.UserName ?? model.UserName,
                    Password = model.Password,
                    TeamName = model.TeamName,
                    Status = status
                });
            }

            var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
            var teams = new List<Team>();
            foreach (var (user, teamName) in created)
            {
                if (teamName is null) continue;

                // Reuse an existing team (local list, then DB) instead of always
                // creating — a re-import otherwise duplicates every team since team
                // Name isn't unique-indexed. Idempotent: join existing, create new.
                var team = teams.Find(t => t.Name == teamName)
                           ?? await dbContext.Teams.Include(t => t.Members)
                               .FirstOrDefaultAsync(t => t.Name == teamName, token);
                if (team is null)
                {
                    team = await teamRepository.CreateTeam(new() { Name = teamName }, user, token);
                    teams.Add(team);
                }
                else
                {
                    if (team.Members.All(m => m.Id != user.Id))
                        team.Members.Add(user);
                    if (!teams.Contains(team))
                        teams.Add(team);
                }
            }

            await teamRepository.SaveAsync(token);
            await trans.CommitAsync(token);

            logger.Log(StaticLocalizer[nameof(Resources.Program.Admin_UserBatchAdded), results.Count(r => r.Status == "created")],
                currentUser, TaskStatus.Success);
        }
        catch
        {
            await trans.RollbackAsync(token);
            throw;
        }

        results.AddRange(skipped);

        return Ok(new CsvImportResultModel
        {
            Total = request.Rows.Count,
            Created = results.Count(r => r.Status == "created"),
            Updated = results.Count(r => r.Status == "updated"),
            Skipped = results.Count(r => r.Status == "skipped"),
            Users = results
        });
    }

    /// <summary>
    /// Batch-send credential emails to imported users
    /// </summary>
    /// <remarks>
    /// Sends one email per item using a single SMTP connection.
    /// Passwords are provided by the caller (from the import result) and are not stored.
    /// Returns sent/failed counts.
    /// </remarks>
    /// <response code="200">Email send complete — returns sent and failed counts</response>
    /// <response code="400">No items provided</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpPost("Users/Credentials/Send")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendCredentialEmails([FromBody] SendCredentialsRequest request,
        CancellationToken token = default)
    {
        if (request.Items is null || request.Items.Count == 0)
            return BadRequest(new RequestResponse("No credentials provided"));

        var mailSender = serviceProvider.GetRequiredService<IMailSender>();
        var globalConfig = serviceProvider.GetRequiredService<IOptionsSnapshot<GlobalConfig>>();
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        // Build (UserName, Email, ResetLink) tuples — one password-reset token per user.
        // Recipients with no matching account are recorded up-front as failures so the
        // response lists every requested item (the UI uses this to offer "resend failed").
        var resetItems = new List<(string UserName, string Email, string ResetLink)>(request.Items.Count);
        var notFound = new List<CredentialSendResult>();
        foreach (var item in request.Items)
        {
            var user = await userManager.FindByEmailAsync(item.Email);
            if (user is null)
            {
                notFound.Add(new CredentialSendResult(item.Email, item.UserName, false, "No user with this email"));
                continue;
            }

            var rawToken = await userManager.GeneratePasswordResetTokenAsync(user);
            var encodedToken = Codec.Base64.Encode(rawToken);
            var encodedEmail = Codec.Base64.Encode(item.Email);
            var resetLink = $"{baseUrl}/account/reset?token={encodedToken}&email={encodedEmail}";
            resetItems.Add((item.UserName, item.Email, resetLink));
        }

        var batch = await mailSender.SendCredentialsBatch(resetItems, baseUrl, localizer, globalConfig, token);

        var sent = batch.Sent;
        var failed = batch.Failed + notFound.Count;
        // Per-recipient outcomes: SMTP results + the not-found set. The UI feeds the
        // failed subset straight back into this endpoint to resend only those.
        var results = batch.Results.Concat(notFound)
            .Select(r => new { email = r.Email, userName = r.UserName, sent = r.Sent, error = r.Error })
            .ToList();

        logger.Log(
            StaticLocalizer[nameof(Resources.Program.Admin_UserBatchAdded), sent],
            await userManager.GetUserAsync(User), TaskStatus.Success);

        return Ok(new { sent, failed, results });
    }

    // ─── CSV import helpers ───────────────────────────────────────────────────

    private static string CsvEnsureUniqueUsername(string desired, HashSet<string> taken, int maxLen = 15)
    {
        var @base = desired.Length > maxLen ? desired[..maxLen] : desired;
        var name = @base;
        for (int i = 1; taken.Contains(name); i++)
        {
            var suf = i.ToString();
            name = (@base.Length + suf.Length > maxLen ? @base[..(maxLen - suf.Length)] : @base) + suf;
        }
        taken.Add(name);
        return name;
    }

    private static string CsvGenerateUsername(string realName, HashSet<string> taken, int maxLen = 15)
    {
        var clean = Regex.Replace(realName.ToLowerInvariant().Replace(" ", "."), @"[^a-z0-9.]", string.Empty);
        var @base = clean.Length > 0 ? (clean.Length > maxLen ? clean[..maxLen] : clean) : "user";
        var name = @base;
        for (int i = 1; taken.Contains(name); i++)
        {
            var suf = i.ToString();
            name = (@base.Length + suf.Length > maxLen ? @base[..(maxLen - suf.Length)] : @base) + suf;
        }
        taken.Add(name);
        return name;
    }

    private static string CsvGeneratePassword()
    {
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string digits = "0123456789";
        const string special = "!@#$%^&*";
        const string all = upper + lower + digits + special;

        var rng = RandomNumberGenerator.GetBytes(32);
        var chars = new char[16];
        // Guarantee at least one character from each required class
        chars[0] = upper[rng[0] % upper.Length];
        chars[1] = lower[rng[1] % lower.Length];
        chars[2] = digits[rng[2] % digits.Length];
        chars[3] = special[rng[3] % special.Length];
        for (int i = 4; i < 16; i++)
            chars[i] = all[rng[i] % all.Length];
        // Fisher-Yates shuffle
        for (int i = 15; i > 0; i--)
        {
            int j = rng[16 + i] % (i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    private static List<string[]> ParseCsvContent(string content)
    {
        var result = new List<string[]>();
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = new List<string>();
            var field = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuote && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuote = !inQuote;
                }
                else if (c == ',' && !inQuote) { fields.Add(field.ToString().Trim()); field.Clear(); }
                else field.Append(c);
            }
            fields.Add(field.ToString().Trim());
            result.Add([.. fields]);
        }
        return result;
    }

    /// <summary>
    /// Search users
    /// </summary>
    /// <remarks>
    /// Use this API to search users, requires Admin permission
    /// </remarks>
    /// <response code="200">User list</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpPost("Users/Search")]
    [ProducesResponseType(typeof(ArrayResponse<UserInfoModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchUsers([FromQuery] string hint, CancellationToken token = default)
    {
        var loweredHint = hint.ToLower();
        var data = await userManager.Users.Where(item =>
            item.UserName!.ToLower().Contains(loweredHint) ||
            item.StdNumber.ToLower().Contains(loweredHint) ||
            item.Email!.ToLower().Contains(loweredHint) ||
            item.PhoneNumber!.ToLower().Contains(loweredHint) ||
            item.Id.ToString().ToLower().Contains(loweredHint) ||
            item.RealName.ToLower().Contains(loweredHint)
        ).OrderBy(e => e.Id).Take(30).ToArrayAsync(token);

        return Ok(data.Select(UserInfoModel.FromUserInfo).ToResponse());
    }

    /// <summary>
    /// Get all team information
    /// </summary>
    /// <remarks>
    /// Use this API to get all teams, requires Admin permission
    /// </remarks>
    /// <response code="200">User list</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpGet("Teams")]
    [ProducesResponseType(typeof(ArrayResponse<TeamInfoModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Teams([FromQuery][Range(0, 500)] int count = 100, [FromQuery] int skip = 0,
        CancellationToken token = default) =>
        Ok((await teamRepository.GetTeams(count, skip, token)).Select(team => TeamInfoModel.FromTeam(team))
            .ToResponse(await teamRepository.CountAsync(token)));

    /// <summary>
    /// Search teams
    /// </summary>
    /// <remarks>
    /// Use this API to search teams, requires Admin permission
    /// </remarks>
    /// <response code="200">User list</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpPost("Teams/Search")]
    [ProducesResponseType(typeof(ArrayResponse<TeamInfoModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchTeams([FromQuery] string hint, CancellationToken token = default) =>
        Ok((await teamRepository.SearchTeams(hint, token))
            .Select(team => TeamInfoModel.FromTeam(team))
            .ToResponse());

    /// <summary>
    /// Modify team information
    /// </summary>
    /// <remarks>
    /// Use this API to modify team information, requires Admin permission
    /// </remarks>
    /// <response code="200">Successfully updated</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    /// <response code="404">Team not found</response>
    [RequireAdmin]
    [HttpPut("Teams/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTeam([FromRoute] int id, [FromBody] AdminTeamModel model,
        CancellationToken token = default)
    {
        var team = await teamRepository.GetTeamById(id, token);

        if (team is null)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Team_NotFound)]));

        team.UpdateInfo(model);
        await teamRepository.SaveAsync(token);

        return Ok();
    }

    /// <summary>
    /// Modify user information
    /// </summary>
    /// <remarks>
    /// Use this API to modify user information, requires Admin permission
    /// </remarks>
    /// <response code="200">Successfully updated</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    /// <response code="404">User not found</response>
    [RequireAdmin]
    [HttpPut("Users/{userid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateUserInfo(string userid, [FromBody] AdminUserInfoModel model)
    {
        var user = await userManager.FindByIdAsync(userid);

        if (user is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Admin_UserNotFound)],
                StatusCodes.Status404NotFound));

        if (model.UserName is not null && model.UserName != user.UserName)
        {
            var result = await userManager.SetUserNameAsync(user, model.UserName);

            if (!result.Succeeded)
                return HandleIdentityError(result.Errors);
        }

        if (model.Email is not null && model.Email != user.Email)
        {
            var result = await userManager.SetEmailAsync(user, model.Email);

            if (!result.Succeeded)
                return HandleIdentityError(result.Errors);
        }

        user.UpdateUserInfo(model);
        await userManager.UpdateAsync(user);

        return Ok();
    }

    /// <summary>
    /// Reset user password
    /// </summary>
    /// <remarks>
    /// Use this API to reset user password, requires Admin permission
    /// </remarks>
    /// <response code="200">Successfully retrieved</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    /// <response code="404">User not found</response>
    [RequireAdmin]
    [HttpDelete("Users/{userid:guid}/Password")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetPassword(string userid)
    {
        var user = await userManager.FindByIdAsync(userid);

        if (user is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Admin_UserNotFound)],
                StatusCodes.Status404NotFound));

        var pwd = Codec.RandomPassword(16);
        var code = await userManager.GeneratePasswordResetTokenAsync(user);
        await userManager.ResetPasswordAsync(user, code, pwd);

        return Ok(pwd);
    }

    /// <summary>
    /// Delete user
    /// </summary>
    /// <remarks>
    /// Use this API to delete user, requires Admin permission
    /// </remarks>
    /// <response code="200">Successfully retrieved</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    /// <response code="404">User not found</response>
    [RequireAdmin]
    [HttpDelete("Users/{userid:guid}")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteUser(Guid userid, CancellationToken token = default)
    {
        var user = await userManager.GetUserAsync(User);

        if (user!.Id == userid)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Admin_SelfDeletionNotAllowed)]));

        user = await userManager.FindByIdAsync(userid.ToString());

        if (user is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Admin_UserNotFound)],
                StatusCodes.Status404NotFound));

        if (await teamRepository.CheckIsCaptain(user, token))
            return BadRequest(
                new RequestResponse(localizer[nameof(Resources.Program.Admin_CaptainDeletionNotAllowed)]));

        // Clear the user's API tokens first: ApiToken.Creator is ON DELETE RESTRICT (the only
        // non-cascade UserInfo FK), so without this userManager.DeleteAsync throws an unhandled
        // DbUpdateException (Postgres 23503) → HTTP 500 and the user is never removed (the
        // `return Ok()` below was dead code on that path). The audit row's purpose is served by
        // deleting the tokens alongside their creator.
        var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
        await dbContext.ApiTokens.Where(t => t.CreatorId == user.Id).ExecuteDeleteAsync(token);

        var result = await userManager.DeleteAsync(user);
        if (!result.Succeeded)
            return BadRequest(new RequestResponse(
                string.Join("; ", result.Errors.Select(e => e.Description))));

        return Ok();
    }

    /// <summary>
    /// Delete team
    /// </summary>
    /// <remarks>
    /// Use this API to delete team, requires Admin permission
    /// </remarks>
    /// <response code="200">Successfully retrieved</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    /// <response code="404">User not found</response>
    [RequireAdmin]
    [HttpDelete("Teams/{id:int}")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTeam(int id, CancellationToken token = default)
    {
        var team = await teamRepository.GetTeamById(id, token);

        if (team is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Team_NotFound)],
                StatusCodes.Status404NotFound));

        await teamRepository.DeleteTeam(team, token);

        return Ok();
    }

    /// <summary>
    /// Get user information
    /// </summary>
    /// <remarks>
    /// Use this API to get user information, requires Admin permission
    /// </remarks>
    /// <response code="200">User object</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpGet("Users/{userid:guid}")]
    [ProducesResponseType(typeof(ProfileUserInfoModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UserInfo(string userid)
    {
        var user = await userManager.FindByIdAsync(userid);

        if (user is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Admin_UserNotFound)],
                StatusCodes.Status404NotFound));

        return Ok(ProfileUserInfoModel.FromUserInfo(user));
    }

    /// <summary>
    /// Get all logs
    /// </summary>
    /// <remarks>
    /// Use this API to get all logs, requires Admin permission
    /// </remarks>
    /// <param name="level"></param>
    /// <param name="count"></param>
    /// <param name="skip"></param>
    /// <param name="search">Search query</param>
    /// <param name="token"></param>
    /// <response code="200">Log list</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpGet("Logs")]
    [ProducesResponseType(typeof(LogMessageModel[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> Logs([FromQuery] string? level = "All",
        [FromQuery][Range(0, 1000)] int count = 50,
        [FromQuery] int skip = 0, [FromQuery] string? search = null, CancellationToken token = default) =>
        Ok(await logRepository.GetLogs(skip, count, level, search, token));

    /// <summary>
    /// Update participation status
    /// </summary>
    /// <remarks>
    /// Use this API to update team participation status, review application, requires Admin permission
    /// </remarks>
    /// <response code="200">Update successful</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    /// <response code="404">Participation object not found</response>
    [RequireUser]
    [HttpPut("Participation/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Participation(int id, [FromBody] ParticipationEditModel model,
        CancellationToken token = default)
    {
        await using var transaction = await participationRepository.BeginTransactionAsync(token);

        var participation = await participationRepository.GetParticipationById(id, token);

        if (participation is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Admin_ParticipationNotFound)],
                StatusCodes.Status404NotFound));

        var currentUser = await userManager.GetUserAsync(User);
        var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
        if (currentUser?.Role != Role.Admin &&
            !await dbContext.EventManagers.AnyAsync(em => em.UserId == currentUser!.Id && em.GameId == participation.GameId, token))
            return Forbid();

        await participationRepository.UpdateParticipation(participation, model, token);

        await transaction.CommitAsync(token);
        await cacheHelper.FlushScoreboardCache(participation.GameId, token);

        // Participation.Status is a scoring input for the A&D/KotH boards too (they filter
        // Accepted), but those cache families don't auto-regenerate once a game is paused or
        // ended — exactly when a disqualification ruling happens. FlushScoreboardCache only
        // covers jeopardy, so flush the A&D/KotH family (incl. frozen, since a status change
        // can predate the freeze) for A&D/KotH games. Cheap no-op otherwise.
        if (await dbContext.GameChallenges.AnyAsync(
                c => c.GameId == participation.GameId
                     && (c.Type == ChallengeType.AttackDefense || c.Type == ChallengeType.KingOfTheHill),
                token))
            await cacheHelper.FlushAdScoreboardCacheIncludingFrozen(participation.GameId, token);

        return Ok();
    }

    /// <summary>
    /// Get per-challenge health stats for a game
    /// </summary>
    /// <remarks>
    /// Returns solve count, wrong attempt count, wrong rate, first-solve time, and solving-team count
    /// for every enabled challenge in the game. Requires GameAdmin permission.
    /// </remarks>
    /// <response code="200">Challenge health stats</response>
    /// <response code="404">Game not found</response>
    [RequireGameAdmin]
    [HttpGet("Games/{id:int}/Challenges/Health")]
    [ProducesResponseType(typeof(ChallengeHealthModel[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetChallengeHealth([FromRoute] int id, CancellationToken token = default)
    {
        var game = await gameRepository.GetGameById(id, token);
        if (game is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Game_NotFound)],
                StatusCodes.Status404NotFound));

        var dbContext = serviceProvider.GetRequiredService<AppDbContext>();

        var challenges = await dbContext.GameChallenges
            .AsNoTracking()
            .Where(c => c.GameId == id && c.IsEnabled)
            .Select(c => new { c.Id, c.Title, Category = c.Category.ToString() })
            .ToListAsync(token);

        var solveStats = await dbContext.Submissions
            .AsNoTracking()
            .Where(s => s.GameId == id && s.Status == AnswerResult.Accepted)
            .GroupBy(s => s.ChallengeId)
            .Select(g => new
            {
                ChallengeId = g.Key,
                Count = g.Count(),
                TeamCount = g.Select(s => s.TeamId).Distinct().Count(),
                FirstTime = g.Min(s => (DateTimeOffset?)s.SubmitTimeUtc)
            })
            .ToListAsync(token);

        var wrongStats = await dbContext.Submissions
            .AsNoTracking()
            .Where(s => s.GameId == id && s.Status == AnswerResult.WrongAnswer)
            .GroupBy(s => s.ChallengeId)
            .Select(g => new { ChallengeId = g.Key, Count = g.Count() })
            .ToListAsync(token);

        var solveMap = solveStats.ToDictionary(x => x.ChallengeId);
        var wrongMap = wrongStats.ToDictionary(x => x.ChallengeId);

        var result = challenges.Select(c => new ChallengeHealthModel
        {
            Id = c.Id,
            Title = c.Title,
            Category = c.Category,
            SolveCount = solveMap.TryGetValue(c.Id, out var s) ? s.Count : 0,
            SolveTeamCount = solveMap.TryGetValue(c.Id, out var st) ? st.TeamCount : 0,
            WrongCount = wrongMap.TryGetValue(c.Id, out var w) ? w.Count : 0,
            FirstSolveTime = solveMap.TryGetValue(c.Id, out var fs) ? fs.FirstTime : null,
        }).ToArray();

        return Ok(result);
    }

    /// <summary>
    /// Get flag-egress events for a game
    /// </summary>
    /// <remarks>
    /// Returns flag-egress events (admin live feed). Each row represents a sliding-window
    /// aggregation of hits where the team's per-team dynamic flag was observed in
    /// proxied container traffic. Requires GameAdmin permission.
    /// </remarks>
    /// <response code="200">Flag egress events page</response>
    /// <response code="404">Game not found</response>
    [RequireGameAdmin]
    [HttpGet("Games/{id:int}/FlagEgress")]
    [ProducesResponseType(typeof(ArrayResponse<FlagEgressEventModel>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFlagEgressEvents(
        [FromRoute] int id,
        [FromQuery] int skip = 0,
        [FromQuery] int count = 50,
        CancellationToken token = default)
    {
        var game = await gameRepository.GetGameById(id, token);
        if (game is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Game_NotFound)],
                StatusCodes.Status404NotFound));

        count = Math.Clamp(count, 1, 200);
        skip = Math.Max(0, skip);

        var dbContext = serviceProvider.GetRequiredService<AppDbContext>();

        var total = await dbContext.FlagEgressEvents.AsNoTracking()
            .Where(e => e.GameId == id)
            .CountAsync(token);

        var rows = await dbContext.FlagEgressEvents.AsNoTracking()
            .Where(e => e.GameId == id)
            .OrderByDescending(e => e.LastSeenUtc)
            .Skip(skip)
            .Take(count)
            .Select(e => new FlagEgressEventModel
            {
                Id = e.Id,
                GameId = e.GameId,
                ParticipationId = e.ParticipationId,
                ChallengeId = e.ChallengeId,
                ContainerId = e.ContainerId,
                TeamName = e.Participation.Team.Name,
                ChallengeTitle = e.Challenge.Title,
                RemoteIp = e.RemoteIp,
                RemotePort = e.RemotePort,
                HitCount = e.HitCount,
                FirstSeenUtc = e.FirstSeenUtc,
                LastSeenUtc = e.LastSeenUtc,
                Direction = e.Direction,
            })
            .ToArrayAsync(token);

        return Ok(new ArrayResponse<FlagEgressEventModel>(rows, total));
    }

    /// <summary>
    /// Get all Writeup basic information
    /// </summary>
    /// <remarks>
    /// Use this API to get Writeup basic information, requires Admin permission
    /// </remarks>
    /// <response code="200">Update successful</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    /// <response code="404">Game not found</response>
    [RequireGameAdmin]
    [HttpGet("Writeups/{id:int}")]
    [ProducesResponseType(typeof(WriteupInfoModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Writeups(int id, CancellationToken token = default)
    {
        var game = await gameRepository.GetGameById(id, token);

        if (game is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Game_NotFound)],
                StatusCodes.Status404NotFound));

        return Ok(await participationRepository.GetWriteups(game, token));
    }

    /// <summary>
    /// Download all Writeups
    /// </summary>
    /// <remarks>
    /// Use this API to download all Writeups, requires Admin permission
    /// </remarks>
    /// <response code="200">Downloaded successfully</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    /// <response code="404">Game not found</response>
    [RequireGameAdmin]
    [HttpGet("Writeups/{id:int}/All")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAllWriteups(int id, CancellationToken token = default)
    {
        var game = await gameRepository.GetGameById(id, token);

        if (game is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Game_NotFound)],
                StatusCodes.Status404NotFound));

        var into = await participationRepository.GetWriteups(game, token);
        var filename = $"Writeups-{game.Title}-{DateTimeOffset.UtcNow:yyyyMMdd-HH.mm.ss}Z";

        return new TarFilesResult(storage, into.Writeups.Select(p => p.File), PathHelper.Uploads, filename, token);
    }

    /// <summary>
    /// Get all container instances
    /// </summary>
    /// <remarks>
    /// Use this API to get all container instances, requires Admin permission
    /// </remarks>
    /// <response code="200">Instance list</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpGet("Instances")]
    [ProducesResponseType(typeof(ArrayResponse<ContainerInstanceModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Instances(CancellationToken token = default) =>
        Ok(new ArrayResponse<ContainerInstanceModel>(await containerRepository.GetContainerInstances(token)));

    /// <summary>
    /// Delete container instance
    /// </summary>
    /// <remarks>
    /// Use this API to forcibly delete container instance, requires Admin permission
    /// </remarks>
    /// <response code="200">Successfully retrieved</response>
    /// <response code="400">Container instance destruction failed</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    /// <response code="404">Container instance not found</response>
    /// <summary>
    /// Sample point-in-time CPU/memory/network stats for a running
    /// container instance. Returns 404 when the instance is gone or the
    /// runtime can't provide stats (e.g. Kubernetes mode in v1).
    /// </summary>
    [RequireAdmin]
    [HttpGet("Instances/{id:guid}/Stats")]
    [ProducesResponseType(typeof(ContainerStatsModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceStats(Guid id,
        [FromServices] Services.Container.Manager.IContainerManager containerManager,
        CancellationToken token = default)
    {
        var container = await containerRepository.GetContainerById(id, token);
        if (container is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Admin_ContainerInstanceNotFound)],
                StatusCodes.Status404NotFound));

        var stats = await containerManager.GetStatsAsync(container, token);
        if (stats is null)
            return NotFound(new RequestResponse("Stats unavailable for this container.",
                StatusCodes.Status404NotFound));

        return Ok(stats);
    }

    [RequireAdmin]
    [HttpDelete("Instances/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    [SuppressMessage("ReSharper", "RouteTemplates.ParameterTypeCanBeMadeStricter")]
    public async Task<IActionResult> DestroyInstance(Guid id, CancellationToken token = default)
    {
        var container = await containerRepository.GetContainerById(id, token);

        if (container is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Admin_ContainerInstanceNotFound)],
                StatusCodes.Status404NotFound));

        if (await containerRepository.DestroyContainer(container, token))
            return Ok();

        return BadRequest(
            new RequestResponse(localizer[nameof(Resources.Program.Admin_ContainerInstanceDestroyFailed)]));
    }

    /// <summary>
    /// Get all files
    /// </summary>
    /// <remarks>
    /// Use this API to get all files, requires Admin permission
    /// </remarks>
    /// <response code="200">File list</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpGet("Files")]
    [ProducesResponseType(typeof(ArrayResponse<LocalFile>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Files([FromQuery][Range(0, 500)] int count = 50, [FromQuery] int skip = 0,
        CancellationToken token = default) =>
        Ok(new ArrayResponse<LocalFile>(await blobService.GetBlobs(count, skip, token)));

    /// <summary>
    /// Get dashboard statistics
    /// </summary>
    /// <remarks>
    /// Use this API to get dashboard statistics, requires Admin permission
    /// </remarks>
    /// <response code="200">Dashboard statistics</response>
    /// <response code="401">Unauthorized user</response>
    /// <response code="403">Forbidden</response>
    [RequireAdmin]
    [HttpGet("Dashboard")]
    [ProducesResponseType(typeof(AdminDashboardModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDashboard(CancellationToken token = default)
    {
        var users = await userManager.Users.CountAsync(token);
        var teams = await teamRepository.CountAsync(token);
        var containers = await containerRepository.CountAsync(token);

        var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
        
        var topGames = await dbContext.Games
             .AsNoTracking()
             .OrderByDescending(g => g.Participations.Count)
             .Take(5)
             .Select(g => new 
             {
                 g.Id,
                 g.Title,
                 g.StartTimeUtc,
                 g.EndTimeUtc,
                 g.PosterHash,
                 g.TeamMemberCountLimit,
                 TeamCount = g.Teams!.Count,
                 UserCount = g.Participations.Count,
             })
             .ToListAsync(token);

        var gameIds = topGames.Select(x => x.Id).ToList();
        
        var reviewStats = await dbContext.ChallengeReviews
            .Where(r => gameIds.Contains(r.GameId))
            .GroupBy(r => r.GameId)
            .Select(g => new 
            {
                GameId = g.Key,
                ReviewCount = g.Count(),
                AverageRating = g.Where(r => r.Rating == ReviewRating.Like || r.Rating == ReviewRating.Dislike)
                    .Average(r => (double?)(r.Rating == ReviewRating.Like ? 1.0 : 0.0))
            })
            .ToDictionaryAsync(x => x.GameId, token);

        var popularGames = topGames.Select(g => new BasicGameInfoModel
        {
             Id = g.Id,
             Title = g.Title,
             StartTimeUtc = g.StartTimeUtc,
             EndTimeUtc = g.EndTimeUtc,
             PosterHash = g.PosterHash,
             TeamMemberCountLimit = g.TeamMemberCountLimit,
             TeamCount = g.TeamCount,
             UserCount = g.UserCount,
             ReviewCount = reviewStats.GetValueOrDefault(g.Id)?.ReviewCount ?? 0,
             AverageRating = reviewStats.GetValueOrDefault(g.Id)?.AverageRating
        }).ToList();

        return Ok(new AdminDashboardModel
        {
            SystemStats = new()
            {
                UserCount = users,
                TeamCount = teams,
                ActiveContainerCount = containers
            },
            TopGames = popularGames
        });
    }

    /// <summary>
    /// Get all reviews
    /// </summary>
    [RequireAdmin]
    [HttpGet("Reviews")]
    [ProducesResponseType(typeof(IEnumerable<ChallengeReviewDetailModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReviews([FromQuery][Range(1, 1000)] int count = 20, [FromQuery] int skip = 0, CancellationToken token = default)
    {
        var reviews = await challengeReviewRepository.GetAllReviewsAsync(count, skip, token);
        return Ok(reviews.Select(ChallengeReviewDetailModel.FromReview)); 
    }

    /// <summary>
    /// Get submission trend
    /// </summary>
    [RequireAdmin]
    [HttpGet("SubmissionTrend")]
    [ProducesResponseType(typeof(IEnumerable<SubmissionTrendModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSubmissionTrend([FromQuery] string range = "Day", CancellationToken token = default)
    {
        var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset since;
        
        switch (range.ToLower())
        {
            case "week":
                since = now.AddDays(-7);
                break;
            case "month":
                since = now.AddDays(-30);
                break;
            case "year":
                since = now.AddYears(-1);
                break;
            case "day":
            default:
                since = now.AddHours(-24);
                break;
        }

        var query = dbContext.Submissions.Where(s => s.SubmitTimeUtc >= since);

        if (range.ToLower() == "year")
        {
            // Group by Month
            return Ok(await query
                .GroupBy(s => new { s.SubmitTimeUtc.Year, s.SubmitTimeUtc.Month })
                .Select(g => new SubmissionTrendModel
                {
                    Time = new DateTime(g.Key.Year, g.Key.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                    Count = g.Count()
                })
                .OrderBy(t => t.Time)
                .ToListAsync(token));
        }
        else if (range.ToLower() == "week" || range.ToLower() == "month")
        {
            // Group by Day
            return Ok(await query
                .GroupBy(s => new { s.SubmitTimeUtc.Year, s.SubmitTimeUtc.Month, s.SubmitTimeUtc.Day })
                .Select(g => new SubmissionTrendModel
                {
                    Time = new DateTime(g.Key.Year, g.Key.Month, g.Key.Day, 0, 0, 0, DateTimeKind.Utc),
                    Count = g.Count()
                })
                .OrderBy(t => t.Time)
                .ToListAsync(token));
        }
        else
        {
            // Group by Hour (Default/Day)
            return Ok(await query
                .GroupBy(s => new { s.SubmitTimeUtc.Year, s.SubmitTimeUtc.Month, s.SubmitTimeUtc.Day, s.SubmitTimeUtc.Hour })
                .Select(g => new SubmissionTrendModel
                {
                    Time = new DateTime(g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, 0, 0, DateTimeKind.Utc),
                    Count = g.Count()
                })
                .OrderBy(t => t.Time)
                .ToListAsync(token));
        }
    }

    /// <summary>
    /// Get all cheat reports
    /// </summary>
    [RequireAdmin]
    [HttpGet("CheatReports")]
    [ProducesResponseType(typeof(IEnumerable<CheatInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCheatReports([FromQuery][Range(1, 1000)] int count = 20, [FromQuery] int skip = 0, CancellationToken token = default)
    {
        var cheats = await cheatInfoRepository.GetAllCheatInfosAsync(count, skip, token);
        return Ok(cheats);
    }

    /// <summary>
    /// Get all writeups
    /// </summary>
    [RequireAdmin]
    [HttpGet("AllWriteups")]
    [ProducesResponseType(typeof(IEnumerable<WriteupInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllWriteups([FromQuery][Range(1, 1000)] int count = 20, [FromQuery] int skip = 0, CancellationToken token = default)
    {
        var writeups = await participationRepository.GetAllWriteupsAsync(count, skip, token);
        return Ok(writeups);
    }

    /// <summary>
    /// List recent anti-cheat blocks (the per-team-user IP / fingerprint
    /// policy enforcement log). Newest first, capped at 200 rows.
    /// </summary>
    [RequireAdmin]
    [HttpGet("AntiCheatBlocks")]
    [ProducesResponseType(typeof(Models.Response.Admin.AntiCheatBlockModel[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAntiCheatBlocks(
        [FromServices] AppDbContext dbContext,
        [FromQuery][Range(1, 500)] int count = 100,
        [FromQuery] int skip = 0,
        CancellationToken token = default)
    {
        var rows = await dbContext.AntiCheatBlocks.AsNoTracking()
            .OrderByDescending(b => b.OccurredAtUtc)
            .Skip(skip).Take(count)
            .Select(b => new Models.Response.Admin.AntiCheatBlockModel
            {
                Id = b.Id,
                UserId = b.UserId,
                UserName = b.UserName,
                ConflictUserId = b.ConflictUserId,
                ConflictUserName = b.ConflictUserName,
                Kind = b.Kind,
                ConflictingValue = b.ConflictingValue,
                OccurredAtUtc = b.OccurredAtUtc
            })
            .ToArrayAsync(token);
        return Ok(rows);
    }

    /// <summary>
    /// Remove an anti-cheat block row. Useful when an admin determines
    /// a false positive (e.g., teammates legitimately share a NAT'd
    /// public IP). The block is purely advisory — deleting it does not
    /// retroactively allow the past login; it just drops the record.
    /// </summary>
    [RequireAdmin]
    [HttpDelete("AntiCheatBlocks/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearAntiCheatBlock(
        [FromRoute] int id, [FromServices] AppDbContext dbContext, CancellationToken token)
    {
        var row = await dbContext.AntiCheatBlocks.FirstOrDefaultAsync(b => b.Id == id, token);
        if (row is null) return NotFound(new RequestResponse("Block not found."));
        dbContext.AntiCheatBlocks.Remove(row);
        await dbContext.SaveChangesAsync(token);
        return Ok();
    }

    // =========================================================
    //  Challenge image build observability
    //  See /root/.claude/plans/compiled-squishing-neumann.md
    // =========================================================

    /// <summary>
    /// Paginated audit history across all challenge builds. Newest
    /// first. Supports filtering by status (Failed by default omitted —
    /// pass <c>status=</c> for the full history) and by game.
    /// </summary>
    [RequireAdmin]
    [HttpGet("Builds")]
    [ProducesResponseType(typeof(Models.Response.Admin.ChallengeBuildAuditModel[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListBuilds(
        [FromServices] AppDbContext dbContext,
        [FromQuery][Range(1, 500)] int count = 50,
        [FromQuery] int skip = 0,
        [FromQuery] ChallengeBuildStatus? status = null,
        [FromQuery] int? gameId = null,
        CancellationToken token = default)
    {
        var q = dbContext.ChallengeBuildAudits.AsNoTracking()
            .Include(a => a.Challenge)
            .OrderByDescending(a => a.EnqueuedAtUtc)
            .AsQueryable();
        if (status is { } s) q = q.Where(a => a.Status == s);
        if (gameId is { } g) q = q.Where(a => a.GameId == g);

        var rows = await q.Skip(skip).Take(count)
            .Select(a => new Models.Response.Admin.ChallengeBuildAuditModel
            {
                Id = a.Id,
                ChallengeId = a.ChallengeId,
                GameId = a.GameId,
                ChallengeTitle = a.Challenge != null ? a.Challenge.Title : string.Empty,
                EnqueuedAtUtc = a.EnqueuedAtUtc,
                StartedAtUtc = a.StartedAtUtc,
                FinishedAtUtc = a.FinishedAtUtc,
                Trigger = a.Trigger,
                Kind = a.Kind,
                Attempt = a.Attempt,
                Status = a.Status,
                Digest = a.Digest,
                ImageRef = a.ImageRef,
                LogTail = a.LogTail,
                ErrorMessage = a.ErrorMessage,
                DurationMs = a.DurationMs
            })
            .ToArrayAsync(token);
        return Ok(rows);
    }

    /// <summary>
    /// Live snapshot of builds currently being processed by a worker.
    /// In-memory only; cleared on app restart.
    /// </summary>
    [RequireAdmin]
    [HttpGet("Builds/InProgress")]
    [ProducesResponseType(typeof(Models.Response.Admin.ChallengeBuildInProgressModel[]), StatusCodes.Status200OK)]
    public IActionResult ListBuildsInProgress(
        [FromServices] Services.Container.Build.IChallengeBuildQueue buildQueue)
    {
        var rows = buildQueue.GetInProgress()
            .OrderByDescending(b => b.StartedAtUtc)
            .Select(b => new Models.Response.Admin.ChallengeBuildInProgressModel
            {
                AuditId = b.AuditId,
                ChallengeId = b.ChallengeId,
                GameId = b.GameId,
                Slug = b.Slug,
                Attempt = b.Attempt,
                Trigger = b.Trigger,
                Kind = b.Kind,
                StartedAtUtc = b.StartedAtUtc
            })
            .ToArray();
        return Ok(rows);
    }

    /// <summary>
    /// Re-enqueue a build for the challenge that owns this audit row.
    /// Convenience action on /admin/builds — under the hood it just
    /// looks up the challenge id from the audit and forwards to the
    /// existing per-challenge Rebuild flow (with all its dedup +
    /// blob-vs-binding-fallback logic).
    /// </summary>
    [RequireAdmin]
    [HttpPost("Builds/{auditId:int}/Reenqueue")]
    [ProducesResponseType(typeof(Models.Response.Admin.ChallengeAuditModel), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReenqueueBuild(
        [FromRoute] int auditId,
        [FromServices] AppDbContext dbContext,
        CancellationToken token)
    {
        var row = await dbContext.ChallengeBuildAudits.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == auditId, token);
        if (row is null) return NotFound(new RequestResponse("Audit row not found."));
        // Redirect to the existing Rebuild route — keeps all the
        // blob-vs-binding fallback + EnqueueResult handling in one
        // place. 307 preserves the POST verb.
        return RedirectPreserveMethod($"/api/edit/games/{row.GameId}/challenges/{row.ChallengeId}/rebuild");
    }

    /// <summary>
    /// Remove a single audit row. Doesn't touch the challenge or its
    /// build artifacts — purely a history cleanup for operators who
    /// don't want a stale Failed entry cluttering /admin/builds.
    /// </summary>
    [RequireAdmin]
    [HttpDelete("Builds/{auditId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteBuildAudit(
        [FromRoute] int auditId,
        [FromServices] AppDbContext dbContext,
        CancellationToken token)
    {
        var row = await dbContext.ChallengeBuildAudits.FirstOrDefaultAsync(a => a.Id == auditId, token);
        if (row is null) return NotFound(new RequestResponse("Audit row not found."));
        dbContext.ChallengeBuildAudits.Remove(row);
        await dbContext.SaveChangesAsync(token);
        return Ok();
    }

    /// <summary>
    /// Bulk-delete every Failed audit row. Lets the operator clear the
    /// noise after a fix without scrolling through and deleting each
    /// row individually. Doesn't affect Building / Queued / Success
    /// rows.
    /// </summary>
    [RequireAdmin]
    [HttpPost("Builds/PruneFailed")]
    [ProducesResponseType(typeof(Models.Response.Admin.PruneResultModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> PruneFailedBuildAudits(
        [FromServices] AppDbContext dbContext, CancellationToken token)
    {
        var deleted = await dbContext.ChallengeBuildAudits
            .Where(a => a.Status == ChallengeBuildStatus.Failed)
            .ExecuteDeleteAsync(token);
        return Ok(new Models.Response.Admin.PruneResultModel { Removed = deleted });
    }

    /// <summary>
    /// Bulk-delete an explicit list of audit row ids — the
    /// select-many-and-Delete UX from <c>/admin/builds</c>. Single
    /// transactional <c>ExecuteDeleteAsync</c> so a 100-row delete
    /// doesn't round-trip per id. Silently no-ops on ids that don't
    /// exist (parallel deletes / page-stale selection).
    /// </summary>
    [RequireAdmin]
    [HttpPost("Builds/BulkDelete")]
    [ProducesResponseType(typeof(Models.Response.Admin.PruneResultModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkDeleteBuildAudits(
        [FromBody] int[] ids,
        [FromServices] AppDbContext dbContext,
        CancellationToken token)
    {
        if (ids is null || ids.Length == 0)
            return BadRequest(new RequestResponse("No ids provided."));
        // Cap per request — keeps the IN-clause bounded and prevents a
        // pathological one-shot delete from holding the table lock.
        if (ids.Length > 500)
            return BadRequest(new RequestResponse("At most 500 ids per request."));

        var deleted = await dbContext.ChallengeBuildAudits
            .Where(a => ids.Contains(a.Id))
            .ExecuteDeleteAsync(token);
        return Ok(new Models.Response.Admin.PruneResultModel { Removed = deleted });
    }

    /// <summary>
    /// Resolve the Docker container provider, or <c>null</c> when the platform is running
    /// in Kubernetes mode (only <c>IContainerProvider&lt;Kubernetes, …&gt;</c> is registered
    /// then — see ContainerServiceExtension). Resolved through <see cref="HttpContext"/>
    /// rather than <c>[FromServices]</c> so an unregistered provider yields a clean 400
    /// instead of a 500 at action-binding time. Image management is a local-daemon concept;
    /// the K8s runtime pulls from a registry and has nothing to list/delete here.
    /// </summary>
    private Services.Container.Provider.IContainerProvider<Docker.DotNet.DockerClient,
        Services.Container.Provider.DockerMetadata>? ResolveDockerProvider() =>
        HttpContext.RequestServices.GetService<
            Services.Container.Provider.IContainerProvider<Docker.DotNet.DockerClient,
                Services.Container.Provider.DockerMetadata>>();

    private static readonly RequestResponse DockerOnlyImageMgmt =
        new("Image management is only available with the Docker container provider.");

    /// <summary>
    /// Garbage-collect <c>gzctf-auto/*</c> images on the local docker
    /// daemon that no live <see cref="GameChallenge.ContainerImage"/>
    /// points at. After the registry-push feature shipped, every
    /// rebuild creates a new content-hashed tag locally; the old ones
    /// stick around forever unless something prunes them. This trims
    /// disk usage on the GZCTF host.
    ///
    /// <para>Images currently referenced by a challenge row are
    /// preserved — even if that row's image is the registry-prefixed
    /// version, the local <c>gzctf-auto/</c> tag is kept too so the
    /// next rebuild can use the deterministic cache path.</para>
    /// </summary>
    [RequireAdmin]
    [HttpPost("Builds/PruneImages")]
    [ProducesResponseType(typeof(Models.Response.Admin.PruneResultModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> PruneOrphanBuildImages(
        [FromServices] AppDbContext dbContext,
        CancellationToken token)
    {
        var dockerProvider = ResolveDockerProvider();
        if (dockerProvider is null) return BadRequest(DockerOnlyImageMgmt);

        // Build the keep-set from current ContainerImage AND AdCheckerImage values.
        // Each can be either the bare local tag (gzctf-auto/...) or the registry-prefixed
        // tag — derive the local form from the registry one so both versions are kept.
        //
        // AdCheckerImage MUST be included: an A&D/KotH challenge's auto-built checker is a
        // gzctf-auto/.../-checker tag that has NO long-running container holding it (it's
        // spawned per tick), so if it falls out of the keep-set this prune deletes it and
        // every subsequent check InternalErrors on the failed pull. Selecting only
        // ContainerImage (the original bug) GC'd live checker images out from under running
        // games. See project_ad_checker_image_pruned.
        var referenced = await dbContext.GameChallenges
            .Where(c => (c.ContainerImage != null && c.ContainerImage.Contains("gzctf-auto/"))
                        || (c.AdCheckerImage != null && c.AdCheckerImage.Contains("gzctf-auto/")))
            .Select(c => new { c.ContainerImage, c.AdCheckerImage })
            .ToListAsync(token);

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void KeepImage(string? img)
        {
            if (string.IsNullOrEmpty(img) || !img.Contains("gzctf-auto/", StringComparison.Ordinal))
                return;
            keep.Add(img); // as stored
            // If it's a registry-prefixed tag, also keep the bare local form.
            var idx = img.IndexOf("gzctf-auto/", StringComparison.Ordinal);
            if (idx > 0) keep.Add(img[idx..]);
        }

        foreach (var row in referenced)
        {
            KeepImage(row.ContainerImage);
            KeepImage(row.AdCheckerImage);
        }

        var client = dockerProvider.GetProvider();
        var images = await client.Images.ListImagesAsync(
            new Docker.DotNet.Models.ImagesListParameters { All = false }, token);

        int removed = 0;
        var messages = new List<string>();
        foreach (var img in images)
        {
            if (img.RepoTags is null) continue;
            // Only touch images whose ONLY tags are gzctf-auto.
            var gzTags = img.RepoTags
                .Where(t => t.Contains("gzctf-auto/") || t.StartsWith("gzctf-auto/", StringComparison.Ordinal))
                .ToArray();
            if (gzTags.Length == 0) continue;
            // If any of this image's tags is referenced, skip the whole image.
            if (gzTags.Any(t => keep.Contains(t))) continue;
            // Untag the gzctf-auto/* tags (leaves any non-gzctf tags alone).
            foreach (var t in gzTags)
            {
                try
                {
                    await client.Images.DeleteImageAsync(t,
                        new Docker.DotNet.Models.ImageDeleteParameters { Force = false, NoPrune = false },
                        token);
                    removed++;
                }
                catch (Exception ex)
                {
                    messages.Add($"{t}: {ex.Message}");
                }
            }
        }

        return Ok(new Models.Response.Admin.PruneResultModel
        {
            Removed = removed,
            Messages = messages.ToArray()
        });
    }

    /// <summary>
    /// List the <c>gzctf-auto/*</c> images present on the local docker daemon, with size,
    /// age, and whether a challenge still references each one. Lets operators see what's
    /// using disk on /admin/builds and delete images individually (vs the blunt
    /// "prune orphans" sweep). Sorted largest-first.
    /// </summary>
    [RequireAdmin]
    [HttpGet("Builds/Images")]
    [ProducesResponseType(typeof(Models.Response.Admin.BuildImageModel[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListBuildImages(
        [FromServices] AppDbContext dbContext,
        CancellationToken token)
    {
        var dockerProvider = ResolveDockerProvider();
        if (dockerProvider is null) return BadRequest(DockerOnlyImageMgmt);

        // Map each referenced gzctf-auto tag (and its bare local form) → the titles of the
        // challenges pointing at it, so the UI can warn before deleting a live image.
        var refs = await dbContext.GameChallenges
            .Where(c => (c.ContainerImage != null && c.ContainerImage.Contains("gzctf-auto/"))
                        || (c.AdCheckerImage != null && c.AdCheckerImage.Contains("gzctf-auto/")))
            .Select(c => new { c.Title, c.ContainerImage, c.AdCheckerImage })
            .ToListAsync(token);

        var tagToTitles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void AddRef(string? img, string title)
        {
            if (string.IsNullOrEmpty(img) || !img.Contains("gzctf-auto/", StringComparison.Ordinal))
                return;
            void Add(string t)
            {
                if (!tagToTitles.TryGetValue(t, out var set))
                    tagToTitles[t] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(title);
            }
            Add(img);
            var idx = img.IndexOf("gzctf-auto/", StringComparison.Ordinal);
            if (idx > 0) Add(img[idx..]);
        }
        foreach (var r in refs)
        {
            AddRef(r.ContainerImage, r.Title);
            AddRef(r.AdCheckerImage, r.Title);
        }

        var client = dockerProvider.GetProvider();
        var images = await client.Images.ListImagesAsync(
            new Docker.DotNet.Models.ImagesListParameters { All = false }, token);

        var rows = new List<Models.Response.Admin.BuildImageModel>();
        foreach (var img in images)
        {
            if (img.RepoTags is null) continue;
            var gzTags = img.RepoTags
                .Where(t => t.Contains("gzctf-auto/", StringComparison.Ordinal))
                .ToArray();
            if (gzTags.Length == 0) continue;

            var titles = gzTags
                .SelectMany(t => tagToTitles.TryGetValue(t, out var s)
                    ? (IEnumerable<string>)s : Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // The auto-built checker repo is gzctf-auto/{game}/{id}-{slug}-checker:{sha};
            // detect by the repo segment (before the tag) ending in "-checker". A tag with
            // no ':' (LastIndexOf == -1) falls back to t.Length so the whole string is tested.
            var isChecker = gzTags.Any(t =>
                t[..(t.LastIndexOf(':') is var c and >= 0 ? c : t.Length)]
                    .EndsWith("-checker", StringComparison.Ordinal));

            rows.Add(new Models.Response.Admin.BuildImageModel
            {
                Id = img.ID,
                Tags = gzTags,
                SizeBytes = img.Size,
                CreatedUtc = img.Created,
                Referenced = titles.Length > 0,
                ReferencedBy = titles,
                IsChecker = isChecker,
            });
        }

        return Ok(rows.OrderByDescending(r => r.SizeBytes).ToArray());
    }

    /// <summary>
    /// Delete a single <c>gzctf-auto/*</c> image (by tag) from the local docker daemon.
    /// Restricted to the platform's own namespace so this can't be used to remove arbitrary
    /// host images. Without <paramref name="force"/> the daemon refuses if a container is
    /// using the image (returns 409); the operator can retry with force=true to override.
    /// </summary>
    [RequireAdmin]
    [HttpDelete("Builds/Images")]
    [ProducesResponseType(typeof(Models.Response.Admin.PruneResultModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteBuildImage(
        [FromQuery] string tag,
        [FromQuery] bool force = false,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(tag) || !tag.Contains("gzctf-auto/", StringComparison.Ordinal))
            return BadRequest(new RequestResponse("Only gzctf-auto/* build images can be deleted here."));

        var dockerProvider = ResolveDockerProvider();
        if (dockerProvider is null) return BadRequest(DockerOnlyImageMgmt);

        // Audit trail for a destructive op: there's no structured audit log yet, so record
        // who deleted which image (and whether it was forced) in the system log.
        logger.SystemLog(
            $"Admin {User.Identity?.Name} deleting build image {tag}{(force ? " (force)" : "")}",
            TaskStatus.Pending, LogLevel.Information);

        var client = dockerProvider.GetProvider();
        try
        {
            var resp = await client.Images.DeleteImageAsync(tag,
                new Docker.DotNet.Models.ImageDeleteParameters { Force = force, NoPrune = false },
                token);
            return Ok(new Models.Response.Admin.PruneResultModel { Removed = resp?.Count ?? 0 });
        }
        catch (Docker.DotNet.DockerApiException e)
            when (e.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return Conflict(new RequestResponse(
                "Image is in use by a container. Stop the container or retry with force.",
                StatusCodes.Status409Conflict));
        }
        catch (Docker.DotNet.DockerImageNotFoundException)
        {
            // Already gone — treat as success so the UI just refreshes the list.
            return Ok(new Models.Response.Admin.PruneResultModel { Removed = 0 });
        }
        catch (Exception e)
        {
            return BadRequest(new RequestResponse(e.Message));
        }
    }

    /// <summary>
    /// Bulk-rebuild every challenge in a game with a broken build: a <c>Failed</c> /
    /// <c>MissingDockerfile</c> service image, OR (for A&amp;D/KotH) a checker image whose
    /// most-recent build failed — checker failures don't flip the service BuildStatus, so
    /// they're pulled in via their audit rows. Each image is rebuilt independently: a
    /// checker-only candidate's healthy service image is left untouched. Skips challenges
    /// with no persisted archive (registry-image or admin-created entries) and reports the
    /// count.
    /// </summary>
    [RequireAdmin]
    [HttpPost("Games/{gameId:int}/BulkRebuild")]
    [ProducesResponseType(typeof(Models.Response.Admin.BulkRebuildResultModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> BulkRebuildFailed(
        [FromRoute] int gameId,
        [FromServices] AppDbContext dbContext,
        [FromServices] Storage.Interface.IBlobStorage storage,
        [FromServices] Services.Container.Build.IChallengeBuildQueue buildQueue,
        CancellationToken token)
    {
        // Service-image failures: the classic "rebuild failed" set.
        var candidates = await dbContext.GameChallenges
            .Where(c => c.GameId == gameId
                        && (c.BuildStatus == ChallengeBuildStatus.Failed
                            || c.BuildStatus == ChallengeBuildStatus.MissingDockerfile))
            .ToListAsync(token);

        // Checker-only failures: a checker build's failure is recorded ONLY on its audit row
        // and never flips the challenge's service BuildStatus (the worker keeps the two
        // images' statuses independent), so a challenge with a healthy service image but a
        // failed/pruned checker is invisible to the query above. Fold in the A&D/KotH
        // challenges whose most-recent Checker-kind audit failed, so the bulk button also
        // restores checker-only breakage. The Checker audit set is small and game-scoped;
        // compute "latest per challenge" in memory to avoid fragile GroupBy→First SQL.
        var checkerAudits = await dbContext.ChallengeBuildAudits
            .Where(a => a.GameId == gameId
                        && a.Kind == Services.Container.Build.ChallengeBuildKind.Checker)
            .OrderByDescending(a => a.EnqueuedAtUtc)
            .Select(a => new { a.ChallengeId, a.Status })
            .ToListAsync(token);
        var checkerFailedIds = checkerAudits
            .GroupBy(a => a.ChallengeId)
            .Where(g => g.First().Status is ChallengeBuildStatus.Failed
                                          or ChallengeBuildStatus.MissingDockerfile)
            .Select(g => g.Key)
            .ToHashSet();
        if (checkerFailedIds.Count > 0)
        {
            var have = candidates.Select(c => c.Id).ToHashSet();
            var extra = await dbContext.GameChallenges
                .Where(c => c.GameId == gameId && checkerFailedIds.Contains(c.Id))
                .ToListAsync(token);
            // have.Add returns false for ids already present → dedups against the service set.
            candidates.AddRange(extra.Where(c => have.Add(c.Id)));
        }

        var result = new Models.Response.Admin.BulkRebuildResultModel();
        var msgs = new List<string>();

        foreach (var ch in candidates)
        {
            if (string.IsNullOrEmpty(ch.OriginalArchiveBlobPath))
            {
                result.Skipped++;
                msgs.Add($"{ch.Title}: no archive on file");
                continue;
            }
            if (!await storage.ExistsAsync(ch.OriginalArchiveBlobPath, token))
            {
                result.Skipped++;
                msgs.Add($"{ch.Title}: archive blob missing");
                continue;
            }

            // Extract once, then (re)build whichever of the two images need it. Failures
            // here are isolated per challenge: skip the one and keep going. A challenge
            // pulled in only for a checker-only failure has a healthy service BuildStatus,
            // so its service image is left alone — only the checker is rebuilt.
            var serviceNeedsRebuild = ch.BuildStatus is ChallengeBuildStatus.Failed
                                                      or ChallengeBuildStatus.MissingDockerfile;
            var workDir = Path.Combine(Path.GetTempPath(), $"gzctf-bulk-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(workDir);
                await using (var src = await storage.OpenReadAsync(ch.OriginalArchiveBlobPath, token))
                {
                    var spool = Path.Combine(workDir, "__archive.bin");
                    await using (var fs = System.IO.File.Create(spool))
                        await src.CopyToAsync(fs, token);
                    await using var sf = System.IO.File.OpenRead(spool);
                    await Services.Transfer.ChallengeImportService.ExtractArchiveAsync(sf, workDir, token);
                    System.IO.File.Delete(spool);
                }

                var topLevel = Directory.EnumerateFileSystemEntries(workDir).Take(2).ToArray();
                var packageDir = topLevel.Length == 1 && Directory.Exists(topLevel[0])
                    ? topLevel[0]
                    : workDir;

                // ── Service image — only when the service build itself failed. Its
                // skip cases (no Dockerfile, already pending) no longer abort the
                // iteration: the checker below must still get a chance to rebuild.
                if (serviceNeedsRebuild)
                {
                    var srcDir = Path.Combine(packageDir, "src");
                    string contextDir = System.IO.File.Exists(Path.Combine(srcDir, "Dockerfile"))
                        ? Path.GetFullPath(srcDir)
                        : Path.GetFullPath(packageDir);
                    const string dockerfile = "Dockerfile";

                    if (!System.IO.File.Exists(Path.Combine(contextDir, dockerfile)))
                    {
                        result.Skipped++;
                        msgs.Add($"{ch.Title}: no Dockerfile in archive");
                    }
                    else if (buildQueue.IsPending(ch.Id))
                    {
                        // bulk action shouldn't pile up duplicate jobs.
                        result.Skipped++;
                        msgs.Add($"{ch.Title}: build already pending");
                    }
                    else
                    {
                        var snap = Path.Combine(Path.GetTempPath(), $"gzctf-build-{Guid.NewGuid():N}");
                        CopyDirRecursive(contextDir, snap);

                        var er = buildQueue.Enqueue(new Services.Container.Build.ChallengeBuildJob(
                            ch.Id, ch.GameId, ch.Title, snap, dockerfile,
                            BuildTrigger.Bulk));
                        if (er == Services.Container.Build.EnqueueResult.Enqueued)
                        {
                            ch.BuildStatus = ChallengeBuildStatus.Queued;
                            ch.LastBuildLog = null;
                            result.Enqueued++;
                        }
                        else
                        {
                            try { Directory.Delete(snap, recursive: true); } catch { /* best effort */ }
                            result.Skipped++;
                            msgs.Add($"{ch.Title}: {(er == Services.Container.Build.EnqueueResult.Rejected ? "queue full" : "already pending")}");
                        }
                    }
                }

                // ── Checker image — independent of the service image. Runs for every A&D/KotH
                // candidate with an auto-built checker, whether it was pulled in for a service
                // failure or a checker-only failure. Best-effort, deduped on (challengeId, Checker).
                if (ch.Type.UsesAdEngine()
                    && Services.Transfer.ChallengeImportService.IsCheckerAutoBuildable(ch.AdCheckerImage)
                    && !buildQueue.IsPending(ch.Id, Services.Container.Build.ChallengeBuildKind.Checker)
                    && Services.Transfer.ChallengeImportService.TryResolveCheckerContext(
                        packageDir, out var checkerCtx, out var checkerDf))
                {
                    var checkerSnap = Path.Combine(Path.GetTempPath(), $"gzctf-build-{Guid.NewGuid():N}");
                    try
                    {
                        CopyDirRecursive(checkerCtx, checkerSnap);
                        var cer = buildQueue.Enqueue(new Services.Container.Build.ChallengeBuildJob(
                            ch.Id, ch.GameId, ch.Title, checkerSnap, checkerDf,
                            BuildTrigger.Bulk, Kind: Services.Container.Build.ChallengeBuildKind.Checker));
                        if (cer == Services.Container.Build.EnqueueResult.Enqueued)
                        {
                            result.Enqueued++;
                            msgs.Add($"{ch.Title}: checker rebuild enqueued");
                        }
                        else
                            try { Directory.Delete(checkerSnap, recursive: true); } catch { /* best effort */ }
                    }
                    catch (Exception cex)
                    {
                        try { Directory.Delete(checkerSnap, recursive: true); } catch { /* best effort */ }
                        msgs.Add($"{ch.Title}: checker enqueue failed — {cex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Skipped++;
                msgs.Add($"{ch.Title}: {ex.Message}");
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
            }
        }

        if (result.Enqueued > 0)
            await dbContext.SaveChangesAsync(token);

        result.Messages = msgs.ToArray();
        return Ok(result);
    }

    static void CopyDirRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
        {
            // Skip symlinks: a link inside the (untrusted) build context would otherwise
            // copy the TARGET's content — host kubeconfig, A&D flags, WireGuard keys — into
            // the snapshot and bake it into the image. Mirrors ChallengeImportService's
            // symlink-stripping copy and the archive extractors. Defense-in-depth: the
            // archive extractor already drops symlink entries, so on-disk contexts reaching
            // here are link-free today, but this no longer depends on that invariant.
            if ((new FileInfo(f).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            System.IO.File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        }
        foreach (var d in Directory.EnumerateDirectories(src))
        {
            if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }

    private IActionResult HandleIdentityError(IEnumerable<IdentityError> errors) =>
        BadRequest(new RequestResponse(errors.FirstOrDefault()?.Description ??
                                       localizer[nameof(Resources.Program.Identity_UnknownError)]));

    // =========================================================
    //  Global repo bindings — multi-event ".gzevent" discovery
    //  See /root/.claude/plans/compiled-squishing-neumann.md
    // =========================================================

    /// <summary>
    /// List configured repo bindings with their child games.
    /// </summary>
    [RequireAdmin]
    [HttpGet("RepoBindings")]
    [ProducesResponseType(typeof(Models.Request.Edit.RepoBindingInfoModel[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListRepoBindings(
        [FromServices] AppDbContext dbContext, CancellationToken token)
    {
        var rows = await dbContext.GameRepoBindings.AsNoTracking()
            .OrderByDescending(b => b.CreatedAtUtc)
            .Select(b => new Models.Request.Edit.RepoBindingInfoModel
            {
                Id = b.Id,
                RepoUrl = b.RepoUrl,
                Ref = b.Ref,
                CreatedAtUtc = b.CreatedAtUtc,
                LastScanUtc = b.LastScanUtc,
                NextScanUtc = b.NextScanUtc,
                IntervalSeconds = b.IntervalSeconds,
                Status = b.Status,
                LastCommitSha = b.LastCommitSha,
                LastScanMessage = b.LastScanMessage,
                HasGitHubToken = b.GitHubTokenEncrypted != null,
                TokenStatus = b.TokenStatus,
                CurrentActivity = b.CurrentActivity,
                PushOnEdit = b.PushOnEdit,
                Games = dbContext.Games
                    .Where(g => g.RepoBindingId == b.Id)
                    .OrderBy(g => g.Title)
                    .Select(g => new Models.Request.Edit.RepoBindingGameSummary
                    {
                        Id = g.Id,
                        Title = g.Title,
                        EventManifestPath = g.EventManifestPath
                    })
                    .ToArray()
            })
            .ToArrayAsync(token);
        return Ok(rows);
    }

    /// <summary>
    /// Register a new repo and immediately scan it for .gzevent manifests.
    /// </summary>
    [RequireAdmin]
    [HttpPost("RepoBindings")]
    [ProducesResponseType(typeof(Models.Request.Edit.RepoBindingScanResultModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateRepoBinding(
        [FromBody] Models.Request.Edit.RepoBindingCreateModel model,
        [FromServices] AppDbContext dbContext,
        [FromServices] IDataProtectionProvider dataProtectionProvider,
        [FromServices] Services.Transfer.RepoBindingDiscoveryService discovery,
        CancellationToken token)
    {
        if (!Services.Transfer.GitHubLocator.TryParse(model.RepoUrl, model.Ref, overrideSubpath: null, out _, out var err))
            return BadRequest(new RequestResponse(err ?? "Invalid github URL."));

        var normalizedUrl = model.RepoUrl.Trim();
        var existing = await dbContext.GameRepoBindings
            .FirstOrDefaultAsync(b => b.RepoUrl == normalizedUrl, token);
        if (existing is not null)
            return Conflict(new RequestResponse(
                $"This repository is already registered (binding id {existing.Id}). Delete or scan that one instead.",
                StatusCodes.Status409Conflict));

        var protector = dataProtectionProvider.CreateProtector(
            Services.Transfer.GameRepoBindingProtection.Purpose);

        var user = (await userManager.GetUserAsync(User))!;

        var clamped = Math.Clamp(model.IntervalSeconds, 60, 86400);
        var hasToken = !string.IsNullOrWhiteSpace(model.GitHubToken);
        var binding = new GameRepoBinding
        {
            RepoUrl = normalizedUrl,
            Ref = string.IsNullOrWhiteSpace(model.Ref) ? null : model.Ref.Trim(),
            GitHubTokenEncrypted = hasToken
                ? protector.Protect(model.GitHubToken!.Trim())
                : null,
            TokenStatus = hasToken ? TokenStatus.Ok : TokenStatus.NotConfigured,
            CreatedByUserId = user.Id,
            IntervalSeconds = clamped,
            Status = RepoWatchStatus.Active,
            // RunImmediately: leave NextScanUtc null so the next poller
            // tick (~30s) picks it up. Otherwise schedule the first run
            // a full interval out.
            NextScanUtc = model.RunImmediately ? null : DateTimeOffset.UtcNow.AddSeconds(clamped)
        };
        dbContext.GameRepoBindings.Add(binding);
        await dbContext.SaveChangesAsync(token);

        // Synchronous first scan when RunImmediately is true so the
        // admin gets immediate feedback. Otherwise the poller will pick
        // it up later.
        if (!model.RunImmediately)
            return Ok(new Models.Request.Edit.RepoBindingScanResultModel());

        // Creation flow: this is the very first scan, no prior
        // LastCommitSha to short-circuit against — force=true is the
        // safe default (avoids a no-op on an empty SHA cell).
        var result = await discovery.ScanAsync(binding.Id, user.Id, token, force: true);
        // discovery.ScanAsync writes LastScanUtc but not NextScanUtc;
        // do that here so the poller doesn't double-scan within the
        // same interval window.
        await dbContext.GameRepoBindings
            .Where(b => b.Id == binding.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextScanUtc,
                DateTimeOffset.UtcNow.AddSeconds(clamped)), token);
        return Ok(new Models.Request.Edit.RepoBindingScanResultModel
        {
            GamesCreated = result.GamesCreated,
            GamesUpdated = result.GamesUpdated,
            ChallengesImported = result.ChallengesImported,
            ChallengesUpdated = result.ChallengesUpdated,
            Failures = result.Failures,
            Messages = result.Messages.ToArray()
        });
    }

    /// <summary>
    /// Update a binding's mutable fields. Every property is optional —
    /// null leaves the existing value alone; <c>GitHubToken</c> follows
    /// the established "" = clear / value = re-protect convention.
    /// Pausing a binding stops the background poller from re-scanning
    /// it without losing the configured interval or token.
    /// </summary>
    [RequireAdmin]
    [HttpPut("RepoBindings/{id:int}")]
    [ProducesResponseType(typeof(Models.Request.Edit.RepoBindingInfoModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRepoBinding(
        [FromRoute] int id,
        [FromBody] Models.Request.Edit.RepoBindingUpdateModel model,
        [FromServices] AppDbContext dbContext,
        [FromServices] IDataProtectionProvider dataProtectionProvider,
        CancellationToken token)
    {
        var binding = await dbContext.GameRepoBindings.FirstOrDefaultAsync(b => b.Id == id, token);
        if (binding is null)
            return NotFound(new RequestResponse("Binding not found."));

        if (model.Ref is not null)
            binding.Ref = string.IsNullOrWhiteSpace(model.Ref) ? null : model.Ref.Trim();
        if (model.IntervalSeconds is { } iv)
            binding.IntervalSeconds = Math.Clamp(iv, 60, 86400);
        if (model.Status is { } st)
        {
            // Resuming from Paused: pull NextScanUtc to "now" so the
            // poller picks it up immediately instead of waiting out the
            // remainder of the previously-scheduled gap.
            if (binding.Status == RepoWatchStatus.Paused && st == RepoWatchStatus.Active)
                binding.NextScanUtc = null;
            binding.Status = st;
        }
        if (model.GitHubToken is not null)
        {
            var protector = dataProtectionProvider.CreateProtector(
                Services.Transfer.GameRepoBindingProtection.Purpose);
            if (string.IsNullOrWhiteSpace(model.GitHubToken))
            {
                binding.GitHubTokenEncrypted = null;
                binding.TokenStatus = TokenStatus.NotConfigured;
            }
            else
            {
                binding.GitHubTokenEncrypted = protector.Protect(model.GitHubToken.Trim());
                binding.TokenStatus = TokenStatus.Ok;
            }
        }
        if (model.PushOnEdit is { } poe)
            binding.PushOnEdit = poe;
        await dbContext.SaveChangesAsync(token);

        return Ok(new Models.Request.Edit.RepoBindingInfoModel
        {
            Id = binding.Id,
            RepoUrl = binding.RepoUrl,
            Ref = binding.Ref,
            CreatedAtUtc = binding.CreatedAtUtc,
            LastScanUtc = binding.LastScanUtc,
            NextScanUtc = binding.NextScanUtc,
            IntervalSeconds = binding.IntervalSeconds,
            Status = binding.Status,
            LastCommitSha = binding.LastCommitSha,
            LastScanMessage = binding.LastScanMessage,
            HasGitHubToken = binding.GitHubTokenEncrypted != null,
            TokenStatus = binding.TokenStatus,
            PushOnEdit = binding.PushOnEdit,
        });
    }

    /// <summary>
    /// Return the most recent scan-history rows for a binding so the
    /// admin can see *which* manifests failed without diving into logs.
    /// Newest first; capped at 20 rows.
    /// </summary>
    [RequireAdmin]
    [HttpGet("RepoBindings/{id:int}/Scans")]
    [ProducesResponseType(typeof(Models.Request.Edit.RepoBindingScanHistoryModel[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRepoBindingScans(
        [FromRoute] int id, [FromServices] AppDbContext dbContext, CancellationToken token)
    {
        var rows = await dbContext.GameRepoBindingScans.AsNoTracking()
            .Where(s => s.BindingId == id)
            .OrderByDescending(s => s.RanAtUtc)
            .Take(20)
            .Select(s => new Models.Request.Edit.RepoBindingScanHistoryModel
            {
                Id = s.Id,
                RanAtUtc = s.RanAtUtc,
                CommitSha = s.CommitSha,
                GamesCreated = s.GamesCreated,
                GamesUpdated = s.GamesUpdated,
                ChallengesImported = s.ChallengesImported,
                ChallengesUpdated = s.ChallengesUpdated,
                Failures = s.Failures,
                Messages = s.Messages
            })
            .ToArrayAsync(token);
        return Ok(rows);
    }

    /// <summary>
    /// Trigger a re-scan of the binding now.
    /// </summary>
    [RequireAdmin]
    [HttpPost("RepoBindings/{id:int}/Scan")]
    [ProducesResponseType(typeof(Models.Request.Edit.RepoBindingScanResultModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> ScanRepoBinding(
        [FromRoute] int id,
        [FromServices] Services.Transfer.RepoBindingDiscoveryService discovery,
        [FromServices] AppDbContext dbContext,
        CancellationToken token)
    {
        var user = (await userManager.GetUserAsync(User))!;
        // Explicit operator action — force re-import even if the SHA
        // hasn't moved. "Scan now" should always do *something* visible
        // (rather than a silent no-op) so the user gets predictable
        // feedback after clicking the button.
        var result = await discovery.ScanAsync(id, user.Id, token, force: true);

        // Reset the poll gate so the background poller waits a full
        // interval before re-running. Without this an Admin "Scan now"
        // + a poller tick a few seconds later would double-scan.
        var iv = await dbContext.GameRepoBindings.AsNoTracking()
            .Where(b => b.Id == id).Select(b => (int?)b.IntervalSeconds)
            .FirstOrDefaultAsync(token) ?? 600;
        var clamped = Math.Clamp(iv, 60, 86400);
        await dbContext.GameRepoBindings
            .Where(b => b.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextScanUtc,
                DateTimeOffset.UtcNow.AddSeconds(clamped)), token);

        return Ok(new Models.Request.Edit.RepoBindingScanResultModel
        {
            GamesCreated = result.GamesCreated,
            GamesUpdated = result.GamesUpdated,
            ChallengesImported = result.ChallengesImported,
            ChallengesUpdated = result.ChallengesUpdated,
            Failures = result.Failures,
            Messages = result.Messages.ToArray()
        });
    }

    /// <summary>
    /// Remove a repo binding. Does NOT delete child games — admin handles those manually.
    /// </summary>
    [RequireAdmin]
    [HttpDelete("RepoBindings/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRepoBinding(
        [FromRoute] int id,
        [FromQuery] bool cascade,
        [FromServices] AppDbContext dbContext,
        [FromServices] Services.Transfer.GitRepoSyncService gitSync,
        CancellationToken token)
    {
        var binding = await dbContext.GameRepoBindings.FirstOrDefaultAsync(b => b.Id == id, token);
        if (binding is null)
            return NotFound(new RequestResponse("Binding not found."));

        var children = await dbContext.Games.Where(g => g.RepoBindingId == id).ToListAsync(token);

        if (cascade)
        {
            // Operator explicitly wants the imported games gone too. Delete each via
            // the full game-deletion path (GameRepository.DeleteGame → RemoveChallenge),
            // NOT a raw Games.RemoveRange. FlagContexts FK to GameChallenges with
            // ON DELETE NO ACTION (not cascade), so relying on the DB cascade alone
            // throws FK_FlagContexts_GameChallenges_ChallengeId (23503) — the
            // "Request failed with status code 500" seen on cascade delete.
            // DeleteGame clears FlagContexts (and the A&D/KotH rows) in order first.
            foreach (var g in children)
            {
                var status = await gameRepository.DeleteGame(g, token);
                if (status != TaskStatus.Success)
                    return StatusCode(StatusCodes.Status500InternalServerError,
                        new RequestResponse($"Failed to delete imported game '{g.Title}'."));
            }
        }
        else
        {
            // Detach: keep the games but null out the binding link so
            // a re-bind can adopt them (see UpsertGameAsync's
            // orphan-adoption pass).
            foreach (var g in children)
            {
                g.RepoBindingId = null;
                g.EventManifestPath = null;
            }
        }

        dbContext.GameRepoBindings.Remove(binding);
        await dbContext.SaveChangesAsync(token);

        // Best-effort: drop the on-disk git clone for this binding so
        // /app/repos doesn't accumulate orphaned checkouts.
        gitSync.DropCache("binding", id);

        return Ok();
    }
}
