using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Identity;

public class SignInManager<TUser> where TUser : class
{
	private sealed class IdentityResultException : Exception
	{
		internal IdentityResult IdentityResult { get; set; }

		public override string Message
		{
			get
			{
				StringBuilder stringBuilder = new StringBuilder("ResetLockout failed.");
				foreach (IdentityError error in IdentityResult.Errors)
				{
					stringBuilder.AppendLine();
					stringBuilder.Append(error.Code);
					stringBuilder.Append(": ");
					stringBuilder.Append(error.Description);
				}
				return stringBuilder.ToString();
			}
		}

		internal IdentityResultException(IdentityResult result)
		{
			IdentityResult = result;
		}
	}

	internal sealed class TwoFactorAuthenticationInfo
	{
		public required TUser User { get; init; }

		public string? LoginProvider { get; init; }
	}

	internal sealed class PasskeyAuthenticationInfo
	{
		public required string? Operation { get; init; }

		public required string? State { get; init; }
	}

	private static class PasskeyOperations
	{
		public const string Attestation = "Attestation";

		public const string Assertion = "Assertion";
	}

	private const string LoginProviderKey = "LoginProvider";

	private const string XsrfKey = "XsrfId";

	private const string PasskeyOperationKey = "PasskeyOperation";

	private const string PasskeyStateKey = "PasskeyState";

	private readonly IHttpContextAccessor _contextAccessor;

	private readonly IAuthenticationSchemeProvider _schemes;

	private readonly IUserConfirmation<TUser> _confirmation;

	private readonly IPasskeyHandler<TUser> _passkeyHandler;

	private readonly SignInManagerMetrics _metrics;

	private HttpContext _context;

	private TwoFactorAuthenticationInfo _twoFactorInfo;

	private PasskeyAuthenticationInfo _passkeyInfo;

	public virtual ILogger Logger { get; set; }

	public UserManager<TUser> UserManager { get; set; }

	public IUserClaimsPrincipalFactory<TUser> ClaimsFactory { get; set; }

	public IdentityOptions Options { get; set; }

	public string AuthenticationScheme { get; set; } = IdentityConstants.ApplicationScheme;

	public HttpContext Context
	{
		get
		{
			HttpContext obj = _context ?? _contextAccessor?.HttpContext;
			if (obj == null)
			{
				throw new InvalidOperationException("HttpContext must not be null.");
			}
			return obj;
		}
		set
		{
			_context = value;
		}
	}

	public SignInManager(UserManager<TUser> userManager, IHttpContextAccessor contextAccessor, IUserClaimsPrincipalFactory<TUser> claimsFactory, IOptions<IdentityOptions> optionsAccessor, ILogger<SignInManager<TUser>> logger, IAuthenticationSchemeProvider schemes, IUserConfirmation<TUser> confirmation)
	{
		ArgumentNullException.ThrowIfNull(userManager, "userManager");
		ArgumentNullException.ThrowIfNull(contextAccessor, "contextAccessor");
		ArgumentNullException.ThrowIfNull(claimsFactory, "claimsFactory");
		UserManager = userManager;
		_contextAccessor = contextAccessor;
		ClaimsFactory = claimsFactory;
		Options = optionsAccessor?.Value ?? new IdentityOptions();
		Logger = logger;
		_schemes = schemes;
		_confirmation = confirmation;
		IMeterFactory meterFactory = userManager.ServiceProvider?.GetService<IMeterFactory>();
		_metrics = ((meterFactory != null) ? new SignInManagerMetrics(meterFactory) : null);
		_passkeyHandler = userManager.ServiceProvider?.GetService<IPasskeyHandler<TUser>>();
	}

	public virtual async Task<ClaimsPrincipal> CreateUserPrincipalAsync(TUser user)
	{
		return await ClaimsFactory.CreateAsync(user);
	}

	public virtual bool IsSignedIn(ClaimsPrincipal principal)
	{
		ArgumentNullException.ThrowIfNull(principal, "principal");
		if (principal.Identities != null)
		{
			return principal.Identities.Any((ClaimsIdentity i) => i.AuthenticationType == AuthenticationScheme);
		}
		return false;
	}

	public virtual async Task<bool> CanSignInAsync(TUser user)
	{
		bool flag = Options.SignIn.RequireConfirmedEmail;
		if (flag)
		{
			flag = !(await UserManager.IsEmailConfirmedAsync(user));
		}
		if (flag)
		{
			Logger.LogDebug(EventIds.UserCannotSignInWithoutConfirmedEmail, "User cannot sign in without a confirmed email.");
			return false;
		}
		flag = Options.SignIn.RequireConfirmedPhoneNumber;
		if (flag)
		{
			flag = !(await UserManager.IsPhoneNumberConfirmedAsync(user));
		}
		if (flag)
		{
			Logger.LogDebug(EventIds.UserCannotSignInWithoutConfirmedPhoneNumber, "User cannot sign in without a confirmed phone number.");
			return false;
		}
		flag = Options.SignIn.RequireConfirmedAccount;
		if (flag)
		{
			flag = !(await _confirmation.IsConfirmedAsync(UserManager, user));
		}
		if (flag)
		{
			Logger.LogDebug(EventIds.UserCannotSignInWithoutConfirmedAccount, "User cannot sign in without a confirmed account.");
			return false;
		}
		return true;
	}

	public virtual async Task RefreshSignInAsync(TUser user)
	{
		long startTimestamp = Stopwatch.GetTimestamp();
		try
		{
			(bool, bool?) obj = await RefreshSignInCoreAsync(user);
			bool item = obj.Item1;
			bool? item2 = obj.Item2;
			SignInResult result = (item ? SignInResult.Success : SignInResult.Failed);
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, result, SignInType.Refresh, item2, startTimestamp);
		}
		catch (Exception exception)
		{
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, null, SignInType.Refresh, null, startTimestamp, exception);
			throw;
		}
	}

	private async Task<(bool success, bool? isPersistent)> RefreshSignInCoreAsync(TUser user)
	{
		AuthenticateResult auth = await Context.AuthenticateAsync(AuthenticationScheme);
		if (auth.Succeeded)
		{
			ClaimsPrincipal? principal = auth.Principal;
			if (principal != null && principal.Identity?.IsAuthenticated == true)
			{
				string authenticatedUserId = UserManager.GetUserId(auth.Principal);
				string text = await UserManager.GetUserIdAsync(user);
				if (authenticatedUserId == null || authenticatedUserId != text)
				{
					Logger.LogError("RefreshSignInAsync prevented because currently authenticated user has a different UserId. Use SignInAsync instead to change users.");
					return (success: false, isPersistent: auth.Properties?.IsPersistent);
				}
				IList<Claim> list = Array.Empty<Claim>();
				Claim claim = auth.Principal?.FindFirst("http://schemas.microsoft.com/ws/2008/06/identity/claims/authenticationmethod");
				Claim claim2 = auth.Principal?.FindFirst("amr");
				if (claim != null || claim2 != null)
				{
					list = new List<Claim>();
					if (claim != null)
					{
						list.Add(claim);
					}
					if (claim2 != null)
					{
						list.Add(claim2);
					}
				}
				await SignInWithClaimsAsync(user, auth.Properties, list);
				return (success: true, isPersistent: auth.Properties?.IsPersistent ?? false);
			}
		}
		Logger.LogError("RefreshSignInAsync prevented because the user is not currently authenticated. Use SignInAsync instead for initial sign in.");
		return (success: false, isPersistent: auth.Properties?.IsPersistent);
	}

	public virtual Task SignInAsync(TUser user, bool isPersistent, string? authenticationMethod = null)
	{
		return SignInAsync(user, new AuthenticationProperties
		{
			IsPersistent = isPersistent
		}, authenticationMethod);
	}

	public virtual Task SignInAsync(TUser user, AuthenticationProperties authenticationProperties, string? authenticationMethod = null)
	{
		IList<Claim> list = Array.Empty<Claim>();
		if (authenticationMethod != null)
		{
			list = new List<Claim>();
			list.Add(new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/authenticationmethod", authenticationMethod));
		}
		return SignInWithClaimsAsync(user, authenticationProperties, list);
	}

	public virtual Task SignInWithClaimsAsync(TUser user, bool isPersistent, IEnumerable<Claim> additionalClaims)
	{
		return SignInWithClaimsAsync(user, new AuthenticationProperties
		{
			IsPersistent = isPersistent
		}, additionalClaims);
	}

	public virtual async Task SignInWithClaimsAsync(TUser user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
	{
		_ = 1;
		try
		{
			ClaimsPrincipal userPrincipal = await CreateUserPrincipalAsync(user);
			foreach (Claim additionalClaim in additionalClaims)
			{
				userPrincipal.Identities.First().AddClaim(additionalClaim);
			}
			if (authenticationProperties == null)
			{
				authenticationProperties = new AuthenticationProperties();
			}
			await Context.SignInAsync(AuthenticationScheme, userPrincipal, authenticationProperties);
			Context.User = userPrincipal;
			_metrics?.SignInUserPrincipal(typeof(TUser).FullName, AuthenticationScheme, authenticationProperties.IsPersistent);
		}
		catch (Exception exception)
		{
			_metrics?.SignInUserPrincipal(typeof(TUser).FullName, AuthenticationScheme, null, exception);
			throw;
		}
	}

	public virtual async Task SignOutAsync()
	{
		_ = 4;
		try
		{
			await Context.SignOutAsync(AuthenticationScheme);
			if (await _schemes.GetSchemeAsync(IdentityConstants.ExternalScheme) != null)
			{
				await Context.SignOutAsync(IdentityConstants.ExternalScheme);
			}
			if (await _schemes.GetSchemeAsync(IdentityConstants.TwoFactorUserIdScheme) != null)
			{
				await Context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
			}
			_metrics?.SignOutUserPrincipal(typeof(TUser).FullName, AuthenticationScheme);
		}
		catch (Exception exception)
		{
			_metrics?.SignOutUserPrincipal(typeof(TUser).FullName, AuthenticationScheme, exception);
			throw;
		}
	}

	public virtual async Task<TUser?> ValidateSecurityStampAsync(ClaimsPrincipal? principal)
	{
		if (principal == null)
		{
			return null;
		}
		TUser user = await UserManager.GetUserAsync(principal);
		if (await ValidateSecurityStampAsync(user, principal.FindFirstValue(Options.ClaimsIdentity.SecurityStampClaimType)))
		{
			return user;
		}
		Logger.LogDebug(EventIds.SecurityStampValidationFailedId4, "Failed to validate a security stamp.");
		return null;
	}

	public virtual async Task<TUser?> ValidateTwoFactorSecurityStampAsync(ClaimsPrincipal? principal)
	{
		if (principal == null || principal.Identity?.Name == null)
		{
			return null;
		}
		TUser user = await UserManager.FindByIdAsync(principal.Identity.Name);
		if (await ValidateSecurityStampAsync(user, principal.FindFirstValue(Options.ClaimsIdentity.SecurityStampClaimType)))
		{
			return user;
		}
		Logger.LogDebug(EventIds.TwoFactorSecurityStampValidationFailed, "Failed to validate a security stamp.");
		return null;
	}

	public virtual async Task<bool> ValidateSecurityStampAsync(TUser? user, string? securityStamp)
	{
		bool flag = user != null;
		if (flag)
		{
			bool flag2 = !UserManager.SupportsUserSecurityStamp;
			if (!flag2)
			{
				flag2 = securityStamp == await UserManager.GetSecurityStampAsync(user);
			}
			flag = flag2;
		}
		return flag;
	}

	public virtual async Task<SignInResult> PasswordSignInAsync(TUser user, string password, bool isPersistent, bool lockoutOnFailure)
	{
		long startTimestamp = Stopwatch.GetTimestamp();
		try
		{
			ArgumentNullException.ThrowIfNull(user, "user");
			SignInResult signInResult = await CheckPasswordSignInAsync(user, password, lockoutOnFailure);
			SignInResult signInResult2 = ((!signInResult.Succeeded) ? signInResult : (await SignInOrTwoFactorAsync(user, isPersistent)));
			SignInResult result = signInResult2;
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, result, SignInType.Password, isPersistent, startTimestamp);
			return result;
		}
		catch (Exception exception)
		{
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, null, SignInType.Password, isPersistent, startTimestamp, exception);
			throw;
		}
	}

	public virtual async Task<SignInResult> PasswordSignInAsync(string userName, string password, bool isPersistent, bool lockoutOnFailure)
	{
		long startTimestamp = Stopwatch.GetTimestamp();
		TUser val = await UserManager.FindByNameAsync(userName);
		if (val == null)
		{
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, SignInResult.Failed, SignInType.Password, isPersistent, startTimestamp);
			return SignInResult.Failed;
		}
		return await PasswordSignInAsync(val, password, isPersistent, lockoutOnFailure);
	}

	public virtual async Task<SignInResult> CheckPasswordSignInAsync(TUser user, string password, bool lockoutOnFailure)
	{
		try
		{
			ArgumentNullException.ThrowIfNull(user, "user");
			SignInResult result = await CheckPasswordSignInCoreAsync(user, password, lockoutOnFailure);
			_metrics?.CheckPasswordSignIn(typeof(TUser).FullName, result);
			return result;
		}
		catch (Exception exception)
		{
			_metrics?.CheckPasswordSignIn(typeof(TUser).FullName, null, exception);
			throw;
		}
	}

	private async Task<SignInResult> CheckPasswordSignInCoreAsync(TUser user, string password, bool lockoutOnFailure)
	{
		SignInResult signInResult = await PreSignInCheck(user);
		if (signInResult != null)
		{
			return signInResult;
		}
		if (await UserManager.CheckPasswordAsync(user, password))
		{
			bool isEnabled;
			bool flag = AppContext.TryGetSwitch("Microsoft.AspNetCore.Identity.CheckPasswordSignInAlwaysResetLockoutOnSuccess", out isEnabled) && isEnabled;
			if (!flag)
			{
				flag = !(await IsTwoFactorEnabledAsync(user));
			}
			bool flag2 = flag;
			if (!flag2)
			{
				flag2 = await IsTwoFactorClientRememberedAsync(user);
			}
			if (flag2 && !(await ResetLockoutWithResult(user)).Succeeded)
			{
				return SignInResult.Failed;
			}
			return SignInResult.Success;
		}
		Logger.LogDebug(EventIds.InvalidPassword, "User failed to provide the correct password.");
		if (UserManager.SupportsUserLockout && lockoutOnFailure)
		{
			if (!((await UserManager.AccessFailedAsync(user)) ?? IdentityResult.Success).Succeeded)
			{
				return SignInResult.Failed;
			}
			if (await UserManager.IsLockedOutAsync(user))
			{
				return await LockedOut(user);
			}
		}
		return SignInResult.Failed;
	}

	public virtual async Task<string> MakePasskeyCreationOptionsAsync(PasskeyUserEntity userEntity)
	{
		ThrowIfNoPasskeyHandler();
		ArgumentNullException.ThrowIfNull(userEntity, "userEntity");
		PasskeyCreationOptionsResult result = await _passkeyHandler.MakeCreationOptionsAsync(userEntity, Context);
		await StorePasskeyAuthenticationInfoAsync("Attestation", result.AttestationState);
		return result.CreationOptionsJson;
	}

	public virtual async Task<string> MakePasskeyRequestOptionsAsync(TUser? user)
	{
		ThrowIfNoPasskeyHandler();
		PasskeyRequestOptionsResult result = await _passkeyHandler.MakeRequestOptionsAsync(user, Context);
		await StorePasskeyAuthenticationInfoAsync("Assertion", result.AssertionState);
		return result.RequestOptionsJson;
	}

	public virtual async Task<PasskeyAttestationResult> PerformPasskeyAttestationAsync(string credentialJson)
	{
		ThrowIfNoPasskeyHandler();
		ArgumentException.ThrowIfNullOrEmpty(credentialJson, "credentialJson");
		PasskeyAuthenticationInfo passkeyAuthenticationInfo = (await RetrievePasskeyAuthenticationInfoAsync()) ?? throw new InvalidOperationException("No passkey attestation is underway. Make sure to call 'SignInManager.MakePasskeyCreationOptionsAsync()' to initiate a passkey attestation.");
		if (!string.Equals("Attestation", passkeyAuthenticationInfo.Operation, StringComparison.Ordinal))
		{
			throw new InvalidOperationException($"Expected passkey operation '{"Attestation"}', but got '{passkeyAuthenticationInfo.Operation}'. This may indicate that you have not previously called '{"SignInManager"}.{"MakePasskeyCreationOptionsAsync"}()'.");
		}
		PasskeyAttestationContext context = new PasskeyAttestationContext
		{
			CredentialJson = credentialJson,
			AttestationState = passkeyAuthenticationInfo.State,
			HttpContext = Context
		};
		PasskeyAttestationResult passkeyAttestationResult = await _passkeyHandler.PerformAttestationAsync(context);
		if (!passkeyAttestationResult.Succeeded)
		{
			Logger.LogDebug(EventIds.PasskeyAttestationFailed, "Passkey attestation failed: {message}", passkeyAttestationResult.Failure.Message);
		}
		return passkeyAttestationResult;
	}

	public virtual async Task<PasskeyAssertionResult<TUser>> PerformPasskeyAssertionAsync(string credentialJson)
	{
		ThrowIfNoPasskeyHandler();
		ArgumentException.ThrowIfNullOrEmpty(credentialJson, "credentialJson");
		PasskeyAuthenticationInfo passkeyAuthenticationInfo = (await RetrievePasskeyAuthenticationInfoAsync()) ?? throw new InvalidOperationException("No passkey assertion is underway. Make sure to call 'SignInManager.MakePasskeyRequestOptionsAsync()' to initiate a passkey assertion.");
		if (!string.Equals("Assertion", passkeyAuthenticationInfo.Operation, StringComparison.Ordinal))
		{
			throw new InvalidOperationException($"Expected passkey operation '{"Assertion"}', but got '{passkeyAuthenticationInfo.Operation}'. This may indicate that you have not previously called '{"SignInManager"}.{"MakePasskeyRequestOptionsAsync"}()'.");
		}
		PasskeyAssertionContext context = new PasskeyAssertionContext
		{
			CredentialJson = credentialJson,
			AssertionState = passkeyAuthenticationInfo.State,
			HttpContext = Context
		};
		PasskeyAssertionResult<TUser> passkeyAssertionResult = await _passkeyHandler.PerformAssertionAsync(context);
		if (!passkeyAssertionResult.Succeeded)
		{
			Logger.LogDebug(EventIds.PasskeyAssertionFailed, "Passkey assertion failed: {message}", passkeyAssertionResult.Failure.Message);
		}
		return passkeyAssertionResult;
	}

	public virtual async Task<SignInResult> PasskeySignInAsync(string credentialJson)
	{
		long startTimestamp = Stopwatch.GetTimestamp();
		try
		{
			SignInResult result = await PasskeySignInCoreAsync(credentialJson);
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, result, SignInType.Passkey, false, startTimestamp);
			return result;
		}
		catch (Exception exception)
		{
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, null, SignInType.Passkey, false, startTimestamp, exception);
			throw;
		}
	}

	private async Task<SignInResult> PasskeySignInCoreAsync(string credentialJson)
	{
		ArgumentException.ThrowIfNullOrEmpty(credentialJson, "credentialJson");
		PasskeyAssertionResult<TUser> assertionResult = await PerformPasskeyAssertionAsync(credentialJson);
		if (!assertionResult.Succeeded)
		{
			return SignInResult.Failed;
		}
		SignInResult signInResult = await PreSignInCheck(assertionResult.User);
		if (signInResult != null)
		{
			return signInResult;
		}
		if (!(await UserManager.AddOrUpdatePasskeyAsync(assertionResult.User, assertionResult.Passkey)).Succeeded)
		{
			return SignInResult.Failed;
		}
		return await SignInOrTwoFactorAsync(assertionResult.User, isPersistent: false, null, bypassTwoFactor: true);
	}

	[MemberNotNull("_passkeyHandler")]
	private void ThrowIfNoPasskeyHandler()
	{
		if (_passkeyHandler == null)
		{
			throw new InvalidOperationException("This operation requires an IPasskeyHandler service to be registered.");
		}
	}

	private async Task StorePasskeyAuthenticationInfoAsync(string operation, string state)
	{
		AuthenticationProperties authenticationProperties = new AuthenticationProperties();
		authenticationProperties.Items["PasskeyOperation"] = operation;
		authenticationProperties.Items["PasskeyState"] = state;
		ClaimsPrincipal principal = new ClaimsPrincipal(new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme));
		await Context.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, principal, authenticationProperties);
	}

	private async Task<PasskeyAuthenticationInfo> RetrievePasskeyAuthenticationInfoAsync()
	{
		PasskeyAuthenticationInfo passkeyAuthenticationInfo = _passkeyInfo;
		if (passkeyAuthenticationInfo == null)
		{
			passkeyAuthenticationInfo = (_passkeyInfo = await RetrievePasskeyInfoCoreAsync());
		}
		return passkeyAuthenticationInfo;
		async Task<PasskeyAuthenticationInfo> RetrievePasskeyInfoCoreAsync()
		{
			AuthenticateResult result = await Context.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
			await Context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
			AuthenticationProperties properties = result.Properties;
			if (properties == null)
			{
				return null;
			}
			if (!properties.Items.TryGetValue("PasskeyOperation", out string value) || !properties.Items.TryGetValue("PasskeyState", out string value2))
			{
				return null;
			}
			return new PasskeyAuthenticationInfo
			{
				Operation = value,
				State = value2
			};
		}
	}

	public virtual async Task<bool> IsTwoFactorClientRememberedAsync(TUser user)
	{
		if (await _schemes.GetSchemeAsync(IdentityConstants.TwoFactorRememberMeScheme) == null)
		{
			return false;
		}
		string userId = await UserManager.GetUserIdAsync(user);
		AuthenticateResult authenticateResult = await Context.AuthenticateAsync(IdentityConstants.TwoFactorRememberMeScheme);
		return authenticateResult?.Principal != null && authenticateResult.Principal.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name") == userId;
	}

	public virtual async Task RememberTwoFactorClientAsync(TUser user)
	{
		_ = 1;
		try
		{
			ClaimsPrincipal principal = await StoreRememberClient(user);
			await Context.SignInAsync(IdentityConstants.TwoFactorRememberMeScheme, principal, new AuthenticationProperties
			{
				IsPersistent = true
			});
			_metrics?.RememberTwoFactorClient(typeof(TUser).FullName, IdentityConstants.TwoFactorRememberMeScheme);
		}
		catch (Exception exception)
		{
			_metrics?.RememberTwoFactorClient(typeof(TUser).FullName, IdentityConstants.TwoFactorRememberMeScheme, exception);
			throw;
		}
	}

	public virtual async Task ForgetTwoFactorClientAsync()
	{
		try
		{
			await Context.SignOutAsync(IdentityConstants.TwoFactorRememberMeScheme);
			_metrics?.ForgetTwoFactorClient(typeof(TUser).FullName, IdentityConstants.TwoFactorRememberMeScheme);
		}
		catch (Exception exception)
		{
			_metrics?.ForgetTwoFactorClient(typeof(TUser).FullName, IdentityConstants.TwoFactorRememberMeScheme, exception);
			throw;
		}
	}

	public virtual async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
	{
		long startTimestamp = Stopwatch.GetTimestamp();
		try
		{
			SignInResult result = await TwoFactorRecoveryCodeSignInCoreAsync(recoveryCode);
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, result, SignInType.TwoFactorRecoveryCode, false, startTimestamp);
			return result;
		}
		catch (Exception exception)
		{
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, null, SignInType.TwoFactorRecoveryCode, false, startTimestamp, exception);
			throw;
		}
	}

	private async Task<SignInResult> TwoFactorRecoveryCodeSignInCoreAsync(string recoveryCode)
	{
		TwoFactorAuthenticationInfo twoFactorInfo = await RetrieveTwoFactorInfoAsync();
		if (twoFactorInfo == null)
		{
			return SignInResult.Failed;
		}
		if ((await UserManager.RedeemTwoFactorRecoveryCodeAsync(twoFactorInfo.User, recoveryCode)).Succeeded)
		{
			return await DoTwoFactorSignInAsync(twoFactorInfo.User, twoFactorInfo, isPersistent: false, rememberClient: false);
		}
		return SignInResult.Failed;
	}

	private async Task<SignInResult> DoTwoFactorSignInAsync(TUser user, TwoFactorAuthenticationInfo twoFactorInfo, bool isPersistent, bool rememberClient)
	{
		if (!(await ResetLockoutWithResult(user)).Succeeded)
		{
			return SignInResult.Failed;
		}
		List<Claim> claims = new List<Claim>
		{
			new Claim("amr", "mfa")
		};
		if (twoFactorInfo.LoginProvider != null)
		{
			claims.Add(new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/authenticationmethod", twoFactorInfo.LoginProvider));
		}
		if (await _schemes.GetSchemeAsync(IdentityConstants.ExternalScheme) != null)
		{
			await Context.SignOutAsync(IdentityConstants.ExternalScheme);
		}
		if (await _schemes.GetSchemeAsync(IdentityConstants.TwoFactorUserIdScheme) != null)
		{
			await Context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
			if (rememberClient)
			{
				await RememberTwoFactorClientAsync(user);
			}
		}
		await SignInWithClaimsAsync(user, isPersistent, claims);
		return SignInResult.Success;
	}

	public virtual async Task<SignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient)
	{
		long startTimestamp = Stopwatch.GetTimestamp();
		try
		{
			SignInResult result = await TwoFactorAuthenticatorSignInCoreAsync(code, isPersistent, rememberClient);
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, result, SignInType.TwoFactorAuthenticator, isPersistent, startTimestamp);
			return result;
		}
		catch (Exception exception)
		{
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, null, SignInType.TwoFactorAuthenticator, isPersistent, startTimestamp, exception);
			throw;
		}
	}

	private async Task<SignInResult> TwoFactorAuthenticatorSignInCoreAsync(string code, bool isPersistent, bool rememberClient)
	{
		TwoFactorAuthenticationInfo twoFactorInfo = await RetrieveTwoFactorInfoAsync();
		if (twoFactorInfo == null)
		{
			return SignInResult.Failed;
		}
		TUser user = twoFactorInfo.User;
		SignInResult signInResult = await PreSignInCheck(user);
		if (signInResult != null)
		{
			return signInResult;
		}
		if (await UserManager.VerifyTwoFactorTokenAsync(user, Options.Tokens.AuthenticatorTokenProvider, code))
		{
			return await DoTwoFactorSignInAsync(user, twoFactorInfo, isPersistent, rememberClient);
		}
		if (UserManager.SupportsUserLockout)
		{
			if (!((await UserManager.AccessFailedAsync(user)) ?? IdentityResult.Success).Succeeded)
			{
				return SignInResult.Failed;
			}
			if (await UserManager.IsLockedOutAsync(user))
			{
				return await LockedOut(user);
			}
		}
		return SignInResult.Failed;
	}

	public virtual async Task<SignInResult> TwoFactorSignInAsync(string provider, string code, bool isPersistent, bool rememberClient)
	{
		long startTimestamp = Stopwatch.GetTimestamp();
		try
		{
			SignInResult result = await TwoFactorSignInCoreAsync(provider, code, isPersistent, rememberClient);
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, result, SignInType.TwoFactor, isPersistent, startTimestamp);
			return result;
		}
		catch (Exception exception)
		{
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, null, SignInType.TwoFactor, isPersistent, startTimestamp, exception);
			throw;
		}
	}

	private async Task<SignInResult> TwoFactorSignInCoreAsync(string provider, string code, bool isPersistent, bool rememberClient)
	{
		TwoFactorAuthenticationInfo twoFactorInfo = await RetrieveTwoFactorInfoAsync();
		if (twoFactorInfo == null)
		{
			return SignInResult.Failed;
		}
		TUser user = twoFactorInfo.User;
		SignInResult signInResult = await PreSignInCheck(user);
		if (signInResult != null)
		{
			return signInResult;
		}
		if (await UserManager.VerifyTwoFactorTokenAsync(user, provider, code))
		{
			return await DoTwoFactorSignInAsync(user, twoFactorInfo, isPersistent, rememberClient);
		}
		if (UserManager.SupportsUserLockout)
		{
			if (!((await UserManager.AccessFailedAsync(user)) ?? IdentityResult.Success).Succeeded)
			{
				return SignInResult.Failed;
			}
			if (await UserManager.IsLockedOutAsync(user))
			{
				return await LockedOut(user);
			}
		}
		return SignInResult.Failed;
	}

	public virtual async Task<TUser?> GetTwoFactorAuthenticationUserAsync()
	{
		TwoFactorAuthenticationInfo twoFactorAuthenticationInfo = await RetrieveTwoFactorInfoAsync();
		if (twoFactorAuthenticationInfo == null)
		{
			return null;
		}
		return twoFactorAuthenticationInfo.User;
	}

	public virtual Task<SignInResult> ExternalLoginSignInAsync(string loginProvider, string providerKey, bool isPersistent)
	{
		return ExternalLoginSignInAsync(loginProvider, providerKey, isPersistent, bypassTwoFactor: false);
	}

	public virtual async Task<SignInResult> ExternalLoginSignInAsync(string loginProvider, string providerKey, bool isPersistent, bool bypassTwoFactor)
	{
		long startTimestamp = Stopwatch.GetTimestamp();
		try
		{
			SignInResult result = await ExternalLoginSignInCoreAsync(loginProvider, providerKey, isPersistent, bypassTwoFactor);
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, result, SignInType.External, isPersistent, startTimestamp);
			return result;
		}
		catch (Exception exception)
		{
			_metrics?.AuthenticateSignIn(typeof(TUser).FullName, AuthenticationScheme, null, SignInType.External, isPersistent, startTimestamp, exception);
			throw;
		}
	}

	private async Task<SignInResult> ExternalLoginSignInCoreAsync(string loginProvider, string providerKey, bool isPersistent, bool bypassTwoFactor)
	{
		TUser user = await UserManager.FindByLoginAsync(loginProvider, providerKey);
		if (user == null)
		{
			return SignInResult.Failed;
		}
		SignInResult signInResult = await PreSignInCheck(user);
		if (signInResult != null)
		{
			return signInResult;
		}
		return await SignInOrTwoFactorAsync(user, isPersistent, loginProvider, bypassTwoFactor);
	}

	public virtual async Task<IEnumerable<AuthenticationScheme>> GetExternalAuthenticationSchemesAsync()
	{
		return (await _schemes.GetAllSchemesAsync()).Where((AuthenticationScheme s) => !string.IsNullOrEmpty(s.DisplayName));
	}

	public virtual async Task<ExternalLoginInfo?> GetExternalLoginInfoAsync(string? expectedXsrf = null)
	{
		AuthenticateResult auth = await Context.AuthenticateAsync(IdentityConstants.ExternalScheme);
		IDictionary<string, string> dictionary = auth?.Properties?.Items;
		if (auth?.Principal == null || dictionary == null || !dictionary.TryGetValue("LoginProvider", out var provider))
		{
			return null;
		}
		if (expectedXsrf != null && (!dictionary.TryGetValue("XsrfId", out var value) || value != expectedXsrf))
		{
			return null;
		}
		string providerKey = auth.Principal.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier") ?? auth.Principal.FindFirstValue("sub");
		if (providerKey == null || provider == null)
		{
			return null;
		}
		string displayName = (await GetExternalAuthenticationSchemesAsync()).FirstOrDefault((AuthenticationScheme p) => p.Name == provider)?.DisplayName ?? provider;
		return new ExternalLoginInfo(auth.Principal, provider, providerKey, displayName)
		{
			AuthenticationTokens = auth.Properties?.GetTokens(),
			AuthenticationProperties = auth.Properties
		};
	}

	public virtual async Task<IdentityResult> UpdateExternalAuthenticationTokensAsync(ExternalLoginInfo externalLogin)
	{
		ArgumentNullException.ThrowIfNull(externalLogin, "externalLogin");
		if (externalLogin.AuthenticationTokens != null && externalLogin.AuthenticationTokens.Any())
		{
			TUser user = await UserManager.FindByLoginAsync(externalLogin.LoginProvider, externalLogin.ProviderKey);
			if (user == null)
			{
				return IdentityResult.Failed();
			}
			foreach (AuthenticationToken authenticationToken in externalLogin.AuthenticationTokens)
			{
				IdentityResult identityResult = await UserManager.SetAuthenticationTokenAsync(user, externalLogin.LoginProvider, authenticationToken.Name, authenticationToken.Value);
				if (!identityResult.Succeeded)
				{
					return identityResult;
				}
			}
		}
		return IdentityResult.Success;
	}

	public virtual AuthenticationProperties ConfigureExternalAuthenticationProperties(string? provider, [StringSyntax("Uri")] string? redirectUrl, string? userId = null)
	{
		AuthenticationProperties authenticationProperties = new AuthenticationProperties
		{
			RedirectUri = redirectUrl
		};
		authenticationProperties.Items["LoginProvider"] = provider;
		if (userId != null)
		{
			authenticationProperties.Items["XsrfId"] = userId;
		}
		return authenticationProperties;
	}

	internal static ClaimsPrincipal StoreTwoFactorInfo(string userId, string? loginProvider)
	{
		ClaimsIdentity claimsIdentity = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
		claimsIdentity.AddClaim(new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", userId));
		if (loginProvider != null)
		{
			claimsIdentity.AddClaim(new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/authenticationmethod", loginProvider));
		}
		return new ClaimsPrincipal(claimsIdentity);
	}

	internal async Task<ClaimsPrincipal> StoreRememberClient(TUser user)
	{
		string value = await UserManager.GetUserIdAsync(user);
		ClaimsIdentity rememberBrowserIdentity = new ClaimsIdentity(IdentityConstants.TwoFactorRememberMeScheme);
		rememberBrowserIdentity.AddClaim(new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", value));
		if (UserManager.SupportsUserSecurityStamp)
		{
			string value2 = await UserManager.GetSecurityStampAsync(user);
			rememberBrowserIdentity.AddClaim(new Claim(Options.ClaimsIdentity.SecurityStampClaimType, value2));
		}
		return new ClaimsPrincipal(rememberBrowserIdentity);
	}

	public virtual async Task<bool> IsTwoFactorEnabledAsync(TUser user)
	{
		bool flag = UserManager.SupportsUserTwoFactor;
		if (flag)
		{
			flag = await UserManager.GetTwoFactorEnabledAsync(user);
		}
		bool flag2 = flag;
		if (flag2)
		{
			flag2 = (await UserManager.GetValidTwoFactorProvidersAsync(user)).Count > 0;
		}
		return flag2;
	}

	protected virtual async Task<SignInResult> SignInOrTwoFactorAsync(TUser user, bool isPersistent, string? loginProvider = null, bool bypassTwoFactor = false)
	{
		bool flag = !bypassTwoFactor;
		if (flag)
		{
			flag = await IsTwoFactorEnabledAsync(user);
		}
		if (flag && !(await IsTwoFactorClientRememberedAsync(user)))
		{
			_twoFactorInfo = new TwoFactorAuthenticationInfo
			{
				User = user,
				LoginProvider = loginProvider
			};
			if (await _schemes.GetSchemeAsync(IdentityConstants.TwoFactorUserIdScheme) != null)
			{
				string userId = await UserManager.GetUserIdAsync(user);
				await Context.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, StoreTwoFactorInfo(userId, loginProvider));
			}
			return SignInResult.TwoFactorRequired;
		}
		if (loginProvider != null)
		{
			await Context.SignOutAsync(IdentityConstants.ExternalScheme);
		}
		if (loginProvider != null)
		{
			await SignInAsync(user, isPersistent, loginProvider);
		}
		else
		{
			await SignInWithClaimsAsync(user, isPersistent, new Claim[1]
			{
				new Claim("amr", "pwd")
			});
		}
		return SignInResult.Success;
	}

	private async Task<TwoFactorAuthenticationInfo> RetrieveTwoFactorInfoAsync()
	{
		if (_twoFactorInfo != null)
		{
			return _twoFactorInfo;
		}
		AuthenticateResult result = await Context.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
		if (result?.Principal == null)
		{
			return null;
		}
		string text = result.Principal.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name");
		if (text == null)
		{
			return null;
		}
		TUser val = await UserManager.FindByIdAsync(text);
		if (val == null)
		{
			return null;
		}
		return new TwoFactorAuthenticationInfo
		{
			User = val,
			LoginProvider = result.Principal.FindFirstValue("http://schemas.microsoft.com/ws/2008/06/identity/claims/authenticationmethod")
		};
	}

	protected virtual async Task<bool> IsLockedOut(TUser user)
	{
		bool flag = UserManager.SupportsUserLockout;
		if (flag)
		{
			flag = await UserManager.IsLockedOutAsync(user);
		}
		return flag;
	}

	protected virtual Task<SignInResult> LockedOut(TUser user)
	{
		Logger.LogDebug(EventIds.UserLockedOut, "User is currently locked out.");
		return Task.FromResult(SignInResult.LockedOut);
	}

	protected virtual async Task<SignInResult?> PreSignInCheck(TUser user)
	{
		if (!(await CanSignInAsync(user)))
		{
			return SignInResult.NotAllowed;
		}
		if (await IsLockedOut(user))
		{
			return await LockedOut(user);
		}
		return null;
	}

	protected virtual async Task ResetLockout(TUser user)
	{
		if (UserManager.SupportsUserLockout)
		{
			IdentityResult identityResult = (await UserManager.ResetAccessFailedCountAsync(user)) ?? IdentityResult.Success;
			if (!identityResult.Succeeded)
			{
				throw new IdentityResultException(identityResult);
			}
		}
	}

	private async Task<IdentityResult> ResetLockoutWithResult(TUser user)
	{
		if (GetType() == typeof(SignInManager<TUser>))
		{
			if (!UserManager.SupportsUserLockout)
			{
				return IdentityResult.Success;
			}
			return (await UserManager.ResetAccessFailedCountAsync(user)) ?? IdentityResult.Success;
		}
		try
		{
			Task task = ResetLockout(user);
			if (task is Task<IdentityResult> task2)
			{
				return (await task2) ?? IdentityResult.Success;
			}
			await task;
			return IdentityResult.Success;
		}
		catch (IdentityResultException ex)
		{
			return ex.IdentityResult;
		}
	}
}
