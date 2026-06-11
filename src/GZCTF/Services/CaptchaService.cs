using System.Security.Cryptography;
using System.Text;
using GZCTF.Extensions;
using GZCTF.Models.Internal;
using GZCTF.Models.Request.Info;
using GZCTF.Services.Cache;
using GZCTF.Utils;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GZCTF.Services;

public interface ICaptchaService
{
    /// <summary>
    /// Verify the captcha response
    /// </summary>
    /// <param name="model">Captcha model</param>
    /// <param name="context">HttpContext</param>
    /// <param name="token"></param>
    /// <returns>Whether the captcha is valid</returns>
    Task<bool> VerifyAsync(ModelWithCaptcha model, HttpContext context, CancellationToken token = default);

    /// <summary>
    /// Get client captcha configuration
    /// </summary>
    /// <returns>Client configuration</returns>
    ClientCaptchaInfoModel ClientInfo();
}

public abstract class ModelWithCaptcha
{
    /// <summary>
    /// Captcha Challenge
    /// </summary>
    public string? Challenge { get; set; }
}

public class CaptchaServiceBase(IOptionsSnapshot<CaptchaConfig> options) : ICaptchaService
{
    // IOptionsSnapshot (scoped), NOT IOptions (singleton): read the captcha config per request
    // so a live /admin/settings change to the provider / keys / difficulty takes effect WITHOUT
    // a process restart. The services are registered scoped and a dispatcher selects the concrete
    // provider by the LIVE Provider value each request — see AddCaptchaService.
    protected readonly CaptchaConfig Config = options.Value;

    public ClientCaptchaInfoModel ClientInfo() => new(Config);

    public virtual Task<bool> VerifyAsync(ModelWithCaptcha model, HttpContext context,
        CancellationToken token = default) =>
        Task.FromResult(true);
}

public sealed class CloudflareTurnstile(IOptionsSnapshot<CaptchaConfig> options, IConfiguration configuration)
    : CaptchaServiceBase(options)
{
    // Static/shared: this service is now SCOPED (per request) so the config can be read live,
    // but a per-request `new HttpClient()` would leak handlers / exhaust sockets. One shared
    // client for the app lifetime is the correct pattern for an outbound verify call.
    private static readonly HttpClient _httpClient = new();

    /// <summary>XOR key used to reverse the obfuscation written by
    /// <c>AdminController.UpdateConfigs</c> at /admin/settings save
    /// time. Captured at construction since the singleton can't take
    /// a scoped service.</summary>
    private readonly byte[] _xorKey = configuration["XorKey"]?.ToUTF8Bytes() ?? [];

    public override async Task<bool> VerifyAsync(ModelWithCaptcha model, HttpContext context,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(Config.SecretKey))
            return true;

        if (string.IsNullOrEmpty(model.Challenge) || context.Connection.RemoteIpAddress is null)
            return false;

        var ip = context.Connection.RemoteIpAddress;

        TurnstileRequestModel req = new()
        {
            Secret = DecryptSecretKey(Config.SecretKey, _xorKey),
            Response = model.Challenge,
            RemoteIp = ip.ToString()
        };

        const string api = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

        var result = await _httpClient.PostAsJsonAsync(api, req, token);
        var res = await result.Content.ReadFromJsonAsync<TurnstileResponseModel>(token);

        return res is not null && res.Success;
    }

    /// <summary>Reverse the XOR + base64 obfuscation applied to
    /// <see cref="CaptchaConfig.SecretKey"/> at save time. Falls back
    /// to the raw stored value when XorKey is empty (test envs) or
    /// the stored value isn't valid base64 (legacy plaintext).</summary>
    internal static string DecryptSecretKey(string? stored, byte[] xorKey)
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
}

public sealed class HashPow(IOptionsSnapshot<CaptchaConfig> options, IDistributedCache cache) :
    CaptchaServiceBase(options)
{
    private const int AnswerLength = 8;

    public override async Task<bool> VerifyAsync(ModelWithCaptcha model, HttpContext context,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(model.Challenge))
            return false;

        var parts = model.Challenge.Split(':');
        if (parts.Length != 2)
            return false;

        var id = parts[0];
        var ans = parts[1];
        if (ans.Length != AnswerLength * 2)
            return false;

        var key = CacheKey.HashPow(id);
        var challenge = await cache.GetAsync(key, token);
        if (challenge is null)
            return false;

        Span<byte> span = stackalloc byte[challenge.Length + AnswerLength];
        challenge.CopyTo(span);
        Convert.FromHexString(ans).CopyTo(span[challenge.Length..]);

        var leadingZeros = SHA256.HashData(span).LeadingZeros();

        var result = leadingZeros >= Config.HashPow.Difficulty;
        if (result)
            await cache.RemoveAsync(key, token);

        return result;
    }
}

/// <summary>
/// The <see cref="ICaptchaService"/> actually injected by controllers. Selects the concrete
/// provider from the LIVE <see cref="CaptchaConfig.Provider"/> on every call, so enabling or
/// rotating captcha at /admin/settings takes effect WITHOUT a process restart. Previously the
/// implementation was bound to the STARTUP provider via <c>AddSingleton&lt;ICaptchaService, T&gt;</c>,
/// so an admin turning captcha on live kept hitting the no-op base service — a silent security bypass.
/// </summary>
public sealed class CaptchaServiceDispatcher(
    IOptionsSnapshot<CaptchaConfig> options,
    IServiceProvider services) : ICaptchaService
{
    private ICaptchaService Active() => options.Value.Provider switch
    {
        CaptchaProvider.HashPow => services.GetRequiredService<HashPow>(),
        CaptchaProvider.CloudflareTurnstile => services.GetRequiredService<CloudflareTurnstile>(),
        _ => services.GetRequiredService<CaptchaServiceBase>()
    };

    public Task<bool> VerifyAsync(ModelWithCaptcha model, HttpContext context, CancellationToken token = default)
        => Active().VerifyAsync(model, context, token);

    public ClientCaptchaInfoModel ClientInfo() => Active().ClientInfo();
}

public static class CaptchaServiceExtension
{
    extension(IServiceCollection services)
    {
        internal IServiceCollection AddCaptchaService(IConfiguration configuration)
        {
            services.Configure<CaptchaConfig>(configuration.GetSection(nameof(CaptchaConfig)));

            // Register all providers + the dispatcher, scoped, instead of binding ICaptchaService
            // to the startup provider. The dispatcher picks by the live provider each request and
            // each provider reads IOptionsSnapshot<CaptchaConfig>, so config is fully live-editable.
            services.AddScoped<CaptchaServiceBase>();
            services.AddScoped<HashPow>();
            services.AddScoped<CloudflareTurnstile>();
            services.AddScoped<ICaptchaService, CaptchaServiceDispatcher>();

            return services;
        }
    }
}
