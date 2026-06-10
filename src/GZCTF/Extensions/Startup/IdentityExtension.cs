using System.Security.Claims;
using System.Text.Json;
using GZCTF.Models.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace GZCTF.Extensions.Startup;

internal static class IdentityExtension
{
    extension(WebApplicationBuilder builder)
    {
        public void ConfigureIdentity()
        {
            builder.Services.AddDataProtection().PersistKeysToDbContext<AppDbContext>();

            var authBuilder = builder.Services.AddAuthentication(o =>
            {
                o.DefaultScheme = IdentityConstants.ApplicationScheme;
                o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            });

            authBuilder.AddIdentityCookies(options =>
            {
                options.ApplicationCookie?.Configure(auth =>
                {
                    auth.Cookie.Name = "GZCTF_Token";
                    auth.SlidingExpiration = true;
                    auth.ExpireTimeSpan = TimeSpan.FromDays(7);
                });
            });

            // External OAuth providers. Registered only when configured (client id +
            // secret present) — see OAuthConfig. Both use the default external sign-in
            // scheme (IdentityConstants.ExternalScheme set above), which the account
            // controller's external-callback flow consumes via SignInManager.
            var oauth = builder.Configuration.GetSection(nameof(OAuthConfig)).Get<OAuthConfig>();
            if (oauth?.GoogleEnabled == true)
                authBuilder.AddGoogle(options =>
                {
                    options.ClientId = oauth.GoogleClientId!;
                    options.ClientSecret = oauth.GoogleClientSecret!;
                    // Surface Google's verified-email flag so the callback can require a
                    // provider-verified email before linking/creating. Newer userinfo uses
                    // "email_verified"; older uses "verified_email".
                    options.Events.OnCreatingTicket = ctx =>
                    {
                        if (IsJsonTrue(ctx.User, "email_verified") || IsJsonTrue(ctx.User, "verified_email"))
                            ctx.Identity?.AddClaim(new Claim(ContextHelper.ExternalEmailVerifiedClaimType, "true"));
                        return Task.CompletedTask;
                    };
                });
            if (oauth?.DiscordEnabled == true)
                authBuilder.AddDiscord(options =>
                {
                    options.ClientId = oauth.DiscordClientId!;
                    options.ClientSecret = oauth.DiscordClientSecret!;
                    // "identify" is requested by default; "email" is needed to read the
                    // address. Discord's user object exposes a boolean "verified" (email
                    // verified) — surface it for the callback's verified-email gate.
                    options.Scope.Add("email");
                    options.Events.OnCreatingTicket = ctx =>
                    {
                        if (IsJsonTrue(ctx.User, "verified"))
                            ctx.Identity?.AddClaim(new Claim(ContextHelper.ExternalEmailVerifiedClaimType, "true"));
                        return Task.CompletedTask;
                    };
                });

            builder.Services.AddIdentityCore<UserInfo>(options =>
                {
                    options.User.RequireUniqueEmail = true;
                    options.Password.RequireNonAlphanumeric = false;
                    options.SignIn.RequireConfirmedEmail = true;

                    // Allow all characters in username
                    options.User.AllowedUserNameCharacters = string.Empty;
                })
                .AddSignInManager<SignInManager<UserInfo>>()
                .AddUserManager<UserManager<UserInfo>>()
                .AddEntityFrameworkStores<AppDbContext>()
                .AddErrorDescriber<TranslatedIdentityErrorDescriber>()
                .AddDefaultTokenProviders();

            builder.Services.Configure<DataProtectionTokenProviderOptions>(o =>
                o.TokenLifespan = TimeSpan.FromHours(3)
            );
        }
    }

    /// <summary>
    /// Whether <paramref name="json"/> has a property <paramref name="key"/> that is a JSON
    /// boolean <c>true</c> (or the string "true"). Used to read provider verified-email flags.
    /// </summary>
    private static bool IsJsonTrue(JsonElement json, string key) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(key, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed) && parsed));
}
