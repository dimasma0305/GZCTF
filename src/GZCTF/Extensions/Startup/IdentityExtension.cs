using AspNet.Security.OAuth.Discord;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

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

            // External OAuth providers (Google + Discord). Registered unconditionally; their
            // credentials come from the DB-backed OAuthConfig via ConfigureExternalOAuthOptions
            // (admin-editable at /admin/settings). The ConfigurationChangeTokenSource ties each
            // scheme's options to the config reload token, so credential changes apply WITHOUT a
            // restart. Both use the default external sign-in scheme (IdentityConstants.ExternalScheme
            // set above), which the account controller's external-callback flow consumes. An
            // unconfigured provider validates as disabled (empty client id) and is never
            // challenged — the ExternalLogin endpoint + the sign-in buttons gate on OAuthConfig.
            authBuilder.AddGoogle(_ => { });
            authBuilder.AddDiscord(_ => { });
            builder.Services.AddSingleton<IConfigureOptions<GoogleOptions>, ConfigureExternalOAuthOptions>();
            builder.Services.AddSingleton<IConfigureOptions<DiscordAuthenticationOptions>, ConfigureExternalOAuthOptions>();
            builder.Services.AddSingleton<IOptionsChangeTokenSource<GoogleOptions>>(
                new ConfigurationChangeTokenSource<GoogleOptions>(
                    GoogleDefaults.AuthenticationScheme, builder.Configuration));
            builder.Services.AddSingleton<IOptionsChangeTokenSource<DiscordAuthenticationOptions>>(
                new ConfigurationChangeTokenSource<DiscordAuthenticationOptions>(
                    DiscordAuthenticationDefaults.AuthenticationScheme, builder.Configuration));

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
}
