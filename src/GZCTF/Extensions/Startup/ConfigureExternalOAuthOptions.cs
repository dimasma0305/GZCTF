using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AspNet.Security.OAuth.Discord;
using GZCTF.Models.Internal;
using GZCTF.Utils;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.Extensions.Options;

namespace GZCTF.Extensions.Startup;

/// <summary>
/// Configures the Google + Discord OAuth handler options from the DB-backed
/// <see cref="OAuthConfig"/> (admin-editable at /admin/settings) rather than baking the
/// credentials in at startup. Paired with a <see cref="ConfigurationChangeTokenSource{TOptions}"/>
/// registration (see <c>IdentityExtension</c>) so the options cache is invalidated when the
/// config reloads — credential changes therefore take effect WITHOUT a restart. Client
/// secrets are XOR-obfuscated at rest (same scheme as EmailConfig.Password) and decoded here.
/// </summary>
internal sealed class ConfigureExternalOAuthOptions(IOptionsMonitor<OAuthConfig> oauth, IConfiguration configuration)
    : IConfigureNamedOptions<GoogleOptions>, IConfigureNamedOptions<DiscordAuthenticationOptions>
{
    private readonly byte[] _xorKey = configuration["XorKey"]?.ToUTF8Bytes() ?? [];

    public void Configure(string? name, GoogleOptions options)
    {
        if (name != GoogleDefaults.AuthenticationScheme)
            return;

        var config = oauth.CurrentValue;
        options.ClientId = config.GoogleClientId ?? string.Empty;
        options.ClientSecret = DecodeSecret(config.GoogleClientSecret);

        // Surface Google's verified-email flag (newer userinfo: email_verified;
        // older: verified_email) so the callback can require a verified email.
        options.Events.OnCreatingTicket = ctx =>
        {
            if (IsJsonTrue(ctx.User, "email_verified") || IsJsonTrue(ctx.User, "verified_email"))
                ctx.Identity?.AddClaim(new Claim(ContextHelper.ExternalEmailVerifiedClaimType, "true"));
            return Task.CompletedTask;
        };
    }

    public void Configure(GoogleOptions options) => Configure(GoogleDefaults.AuthenticationScheme, options);

    public void Configure(string? name, DiscordAuthenticationOptions options)
    {
        if (name != DiscordAuthenticationDefaults.AuthenticationScheme)
            return;

        var config = oauth.CurrentValue;
        options.ClientId = config.DiscordClientId ?? string.Empty;
        options.ClientSecret = DecodeSecret(config.DiscordClientSecret);

        // "identify" is requested by default; "email" is needed to read the address.
        if (!options.Scope.Contains("email"))
            options.Scope.Add("email");

        // Discord's user object exposes a boolean "verified" (email verified).
        options.Events.OnCreatingTicket = ctx =>
        {
            if (IsJsonTrue(ctx.User, "verified"))
                ctx.Identity?.AddClaim(new Claim(ContextHelper.ExternalEmailVerifiedClaimType, "true"));
            return Task.CompletedTask;
        };
    }

    public void Configure(DiscordAuthenticationOptions options) =>
        Configure(DiscordAuthenticationDefaults.AuthenticationScheme, options);

    /// <summary>
    /// Decode an XOR+base64 client secret. Falls back to the raw stored value when no XorKey
    /// is configured (test envs) — mirrors MailSender.DecryptPassword.
    /// </summary>
    private string DecodeSecret(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;
        if (_xorKey.Length == 0)
            return stored;
        try
        {
            return Encoding.UTF8.GetString(Codec.Xor(Convert.FromBase64String(stored), _xorKey));
        }
        catch
        {
            return stored;
        }
    }

    private static bool IsJsonTrue(JsonElement json, string key) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(key, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed) && parsed));
}
