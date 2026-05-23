using System.Collections.Concurrent;
using System.Net.Security;
using System.Text;
using GZCTF.Models.Internal;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace GZCTF.Services.Mail;

public sealed class MailSender : IMailSender, IDisposable
{
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ILogger<MailSender> _logger;
    private readonly ConcurrentQueue<MailContent> _mailQueue = new();
    private EmailConfig? _options;
    private readonly AsyncManualResetEvent _resetEvent = new();
    private SmtpClient? _smtpClient;
    private readonly object _clientLock = new();
    /// <summary>Set by the options-monitor callback; checked by the worker loop
    /// before each send so config changes from /admin/settings take effect
    /// without a service restart.</summary>
    private volatile bool _configDirty;
    private readonly IDisposable? _optionsChangeListener;
    private bool _disposed;

    /// <summary>XOR key used to reverse the obfuscation applied to
    /// EmailConfig.Password at /admin/settings save time. Captured at
    /// construction since the singleton can't take a scoped service.</summary>
    private readonly byte[] _xorKey;

    public MailSender(
        IOptions<AccountPolicy> accountPolicy,
        IOptionsMonitor<EmailConfig> optionsMonitor,
        IConfiguration configuration,
        ILogger<MailSender> logger)
    {
        _logger = logger;
        _options = optionsMonitor.CurrentValue;
        _cancellationToken = _cancellationTokenSource.Token;
        _xorKey = configuration["XorKey"]?.ToUTF8Bytes() ?? [];

        // Live-reload: when /admin/settings writes a new EmailConfig
        // the OptionsMonitor fires; flip the dirty flag and the worker
        // tears down + rebuilds the SmtpClient on the next iteration.
        _optionsChangeListener = optionsMonitor.OnChange(newOpts =>
        {
            _options = newOpts;
            _configDirty = true;
            _resetEvent.Set();
            _logger.SystemLog(
                "EmailConfig changed; SmtpClient will rebuild on next send",
                TaskStatus.Pending, LogLevel.Information);
        });

        if (!TryBuildClient(out var startupErr))
        {
            if (accountPolicy.Value.EmailConfirmationRequired && startupErr is FatalConfig)
                ExitWithFatalMessage(StaticLocalizer[nameof(Resources.Program.MailSender_InvalidEmailConfig)]);
            // Not fatal — start the worker anyway so future config
            // changes from /admin/settings can bring the sender online.
        }

        Task.Factory.StartNew(MailSenderWorker, _cancellationToken, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Sentinel returned by <see cref="TryBuildClient"/> to
    /// distinguish "config is fundamentally bad" from "client just
    /// disconnected" — only the former should kill the process when
    /// EmailConfirmationRequired is on.</summary>
    private enum FatalConfig { Yes }

    /// <summary>(Re)build the SMTP client from the current
    /// <see cref="_options"/>. Returns false if the config is
    /// incomplete or the connection test fails. Safe to call from
    /// multiple threads; the lock serializes rebuilds.</summary>
    private bool TryBuildClient(out object? err)
    {
        err = null;
        lock (_clientLock)
        {
            _smtpClient?.Dispose();
            _smtpClient = null;

            var opts = _options;
            if (opts is null ||
                string.IsNullOrWhiteSpace(opts.SenderAddress) ||
                string.IsNullOrWhiteSpace(opts.Smtp?.Host) || opts.Smtp.Port <= 0)
            {
                err = FatalConfig.Yes;
                return false;
            }

            var client = BuildSmtpClient(opts);

            // Test the connection synchronously here so misconfig is
            // surfaced immediately rather than at first send.
            try
            {
                client.Connect(opts.Smtp.Host, opts.Smtp.Port);
                client.Authenticate(opts.UserName, DecryptPassword(opts.Password));
                client.Disconnect(true);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "{msg}",
                    StaticLocalizer[nameof(Resources.Program.MailSender_MailSendFailed)]);
                client.Dispose();
                return false;
            }

            _smtpClient = client;
            _configDirty = false;
            _logger.SystemLog(StaticLocalizer[nameof(Resources.Program.MailSender_ConnectedToSmtp),
                $"{opts.Smtp.Host}:{opts.Smtp.Port}"], TaskStatus.Success, LogLevel.Debug);
            return true;
        }
    }

    /// <summary>Construct an SmtpClient pre-configured with the cert
    /// policy + cipher suites used by every code path in this class.
    /// Caller owns the lifecycle (Connect / Authenticate / Send /
    /// Disconnect / Dispose).</summary>
    private static SmtpClient BuildSmtpClient(EmailConfig opts)
    {
        var client = new SmtpClient();
        client.AuthenticationMechanisms.Remove("XOAUTH2");

        if (!OperatingSystem.IsWindows())
            // Some systems may not enable old (non-recommend) ciphers in TLS configuration and lead to failures when
            // connecting to some SMTP servers, override the default policy to include all ciphers except MD5, SHA1, and NULL
            client.SslCipherSuitesPolicy = new CipherSuitesPolicy(Enum.GetValues<TlsCipherSuite>()
                .Where(cipher =>
                {
                    var cipherName = cipher.ToString();
                    // Exclude MD5, SHA1, and NULL ciphers for security reasons
                    return !cipherName.EndsWith("MD5") && !cipherName.EndsWith("SHA") &&
                           !cipherName.EndsWith("NULL");
                }));

        client.ServerCertificateValidationCallback = (_, _, _, errors)
            => errors is SslPolicyErrors.None || opts.Smtp?.BypassCertVerify is true;

        return client;
    }

    /// <summary>Reverse the XOR + base64 obfuscation applied to
    /// EmailConfig.Password by <see cref="AdminController.UpdateConfigs"/>.
    /// Falls back to the raw stored value when XorKey is empty (test envs)
    /// or when the stored value isn't valid base64 (legacy plaintext from
    /// before the obfuscation landed).</summary>
    private string DecryptPassword(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (_xorKey.Length == 0) return stored;
        try
        {
            return Encoding.UTF8.GetString(
                Codec.Xor(Convert.FromBase64String(stored), _xorKey));
        }
        catch
        {
            return stored;
        }
    }

    /// <summary>One-shot SMTP smoke test driven by the
    /// /admin/settings "Send test" button. Builds a short-lived
    /// SmtpClient from the supplied config (does NOT touch the
    /// singleton's <see cref="_smtpClient"/>), sends a single plain-
    /// text message, and reports the outcome. The caller is
    /// responsible for resolving the plaintext password — forms
    /// arrive with operator-typed plaintext; the controller falls
    /// back to <see cref="DecryptPassword"/> on the stored value
    /// when the form's password field is blank.</summary>
    public async Task<(bool Ok, string? Error)> TestSendAsync(
        EmailConfig config, string passwordPlain, string recipient, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(config.SenderAddress) ||
            string.IsNullOrWhiteSpace(config.Smtp?.Host) || config.Smtp.Port <= 0)
            return (false, "SMTP host, port and sender address are required");

        try
        {
            using var client = BuildSmtpClient(config);
            await client.ConnectAsync(config.Smtp.Host, config.Smtp.Port, cancellationToken: token);

            if (!string.IsNullOrEmpty(config.UserName))
                await client.AuthenticateAsync(config.UserName, passwordPlain, token);

            var senderName = string.IsNullOrWhiteSpace(config.SenderName) ? "GZCTF" : config.SenderName;
            using var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(senderName, config.SenderAddress));
            msg.To.Add(MailboxAddress.Parse(recipient));
            msg.Subject = "GZCTF SMTP test";
            msg.Body = new TextPart(TextFormat.Plain)
            {
                Text = "This is a test message sent from /admin/settings. SMTP delivery is working."
            };

            await client.SendAsync(msg, token);
            await client.DisconnectAsync(true, token);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellationTokenSource.Cancel();
        _optionsChangeListener?.Dispose();
        lock (_clientLock)
        {
            _smtpClient?.Dispose();
            _smtpClient = null;
        }
        GC.SuppressFinalize(this);
    }

    public async Task SendMailContent(MailContent content)
    {
        // TODO: use GlobalConfig.DefaultEmailTemplate
        // TODO: use a string formatter library
        // TODO: update default template with new names
        var emailContent = new StringBuilder(content.Template)
            .Replace("{title}", content.Title)
            .Replace("{information}", content.Information)
            .Replace("{btnmsg}", content.ButtonMessage)
            .Replace("{email}", content.Email)
            .Replace("{userName}", content.UserName)
            .Replace("{url}", content.Url)
            .Replace("{nowtime}", content.Time)
            .Replace("{platform}", content.Platform)
            .ToString();

        var title = $"{content.Title} - {content.Platform}";

        var sender = string.IsNullOrWhiteSpace(_options!.SenderName) ? content.Platform : _options.SenderName;

        // SenderAddress is checked in constructor, so it won't be null here
        var from = new MailboxAddress(sender, _options.SenderAddress!);

        var to = new MailboxAddress(content.UserName, content.Email);

        if (!await SendEmailAsync(title, emailContent, from, to))
            _logger.SystemLog(StaticLocalizer[nameof(Resources.Program.MailSender_MailSendFailed)],
                TaskStatus.Failed);
    }

    public bool SendConfirmEmailUrl(string? userName, string? email, string? confirmLink,
        IStringLocalizer<Program> localizer, IOptionsSnapshot<GlobalConfig> options) =>
        EnqueueMailTask(userName, email, confirmLink, MailType.ConfirmEmail, localizer, options);

    public bool SendChangeEmailUrl(string? userName, string? email, string? resetLink,
        IStringLocalizer<Program> localizer, IOptionsSnapshot<GlobalConfig> options) =>
        EnqueueMailTask(userName, email, resetLink, MailType.ChangeEmail, localizer, options);

    public bool SendResetPasswordUrl(string? userName, string? email, string? resetLink,
        IStringLocalizer<Program> localizer, IOptionsSnapshot<GlobalConfig> options) =>
        EnqueueMailTask(userName, email, resetLink, MailType.ResetPassword, localizer, options);

    public async Task<(int Sent, int Failed)> SendCredentialsBatch(
        IEnumerable<(string UserName, string Email, string ResetLink)> items,
        string loginUrl,
        IStringLocalizer<Program> localizer,
        IOptionsSnapshot<GlobalConfig> options,
        CancellationToken token = default)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return (0, 0);

        if (_options?.Smtp?.Host is null || !(_options.Smtp.Port > 0) ||
            string.IsNullOrWhiteSpace(_options.SenderAddress))
            return (0, list.Count);

        var template = localizer[nameof(Resources.Program.MailSender_Template)].Value;
        var platform = options.Value.Platform;
        var sender = string.IsNullOrWhiteSpace(_options.SenderName) ? platform : _options.SenderName;
        var from = new MailboxAddress(sender, _options.SenderAddress);
        var nowTime = DateTimeOffset.UtcNow.ToString("u");

        int sent = 0, failed = 0;

        using var client = new SmtpClient();
        client.AuthenticationMechanisms.Remove("XOAUTH2");
        client.ServerCertificateValidationCallback = (_, _, _, errors)
            => errors is SslPolicyErrors.None || _options.Smtp.BypassCertVerify is true;

        if (!OperatingSystem.IsWindows())
            client.SslCipherSuitesPolicy = new CipherSuitesPolicy(Enum.GetValues<TlsCipherSuite>()
                .Where(cipher =>
                {
                    var n = cipher.ToString();
                    return !n.EndsWith("MD5") && !n.EndsWith("SHA") && !n.EndsWith("NULL");
                }));

        try
        {
            await client.ConnectAsync(_options.Smtp.Host, _options.Smtp.Port, cancellationToken: token);
            await client.AuthenticateAsync(_options.UserName, DecryptPassword(_options.Password), token);

            foreach (var (userName, email, resetLink) in list)
            {
                var info =
                    $"<p>An account has been created for you on <strong>{platform}</strong>.</p>" +
                    $"<p><strong>Username:</strong> <code>{userName}</code></p>" +
                    "<p>Click the button below to set your own password. " +
                    "This link is valid for 24 hours and can only be used once.</p>";

                var body = new StringBuilder(template)
                    .Replace("{title}", "Set Your Password")
                    .Replace("{information}", info)
                    .Replace("{btnmsg}", "Set My Password")
                    .Replace("{email}", email)
                    .Replace("{userName}", userName)
                    .Replace("{url}", resetLink)
                    .Replace("{nowtime}", nowTime)
                    .Replace("{platform}", platform)
                    .ToString();

                using var msg = new MimeMessage();
                msg.From.Add(from);
                msg.To.Add(new MailboxAddress(userName, email));
                msg.Subject = $"Set Your Password - {platform}";
                msg.Body = new TextPart(TextFormat.Html) { Text = body };

                try
                {
                    await client.SendAsync(msg, token);
                    sent++;
                    _logger.SystemLog(StaticLocalizer[nameof(Resources.Program.MailSender_SendMail), email],
                        TaskStatus.Success, LogLevel.Information);
                }
                catch (Exception e)
                {
                    failed++;
                    _logger.LogErrorMessage(e, StaticLocalizer[nameof(Resources.Program.MailSender_MailSendFailed)]);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogErrorMessage(e, StaticLocalizer[nameof(Resources.Program.MailSender_MailSendFailed)]);
            failed += list.Count - sent - failed;
        }
        finally
        {
            try { await client.DisconnectAsync(true, token); } catch { }
        }

        return (sent, failed);
    }

    private async Task<bool> SendEmailAsync(string subject, string content, MailboxAddress from, MailboxAddress to)
    {
        if (_smtpClient is null)
            return false;

        using var msg = new MimeMessage();
        msg.From.Add(from);
        msg.To.Add(to);
        msg.Subject = subject;
        msg.Body = new TextPart(TextFormat.Html) { Text = content };

        try
        {
            await _smtpClient.SendAsync(msg, _cancellationToken);

            _logger.SystemLog(StaticLocalizer[nameof(Resources.Program.MailSender_SendMail), to],
                TaskStatus.Success, LogLevel.Information);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogErrorMessage(e, StaticLocalizer[nameof(Resources.Program.MailSender_MailSendFailed)]);
            return false;
        }
    }

    private async Task MailSenderWorker()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            await _resetEvent.WaitAsync(_cancellationToken);
            _resetEvent.Reset();

            // Pick up any pending config change before this batch.
            if (_configDirty)
                TryBuildClient(out _);

            // No client + nothing we can do — drop the batch with a
            // log so the operator sees why. The next config change
            // will restart this loop.
            if (_smtpClient is null)
            {
                if (!_mailQueue.IsEmpty)
                {
                    _logger.SystemLog(
                        "Mail queue drained without sending — SMTP not configured. " +
                        "Set EmailConfig in /admin/settings.",
                        TaskStatus.Failed, LogLevel.Warning);
                    _mailQueue.Clear();
                }
                continue;
            }

            try
            {
                if (!_smtpClient.IsConnected)
                    await _smtpClient.ConnectAsync(_options!.Smtp!.Host, _options.Smtp.Port,
                        cancellationToken: _cancellationToken);

                if (!_smtpClient.IsAuthenticated)
                    await _smtpClient.AuthenticateAsync(_options!.UserName, DecryptPassword(_options.Password),
                        _cancellationToken);

                while (_mailQueue.TryDequeue(out var content))
                    await SendMailContent(content);
            }
            catch (Exception e)
            {
                // Failed to establish SMTP connection, clear the queue
                _mailQueue.Clear();

                _logger.LogErrorMessage(e, StaticLocalizer[nameof(Resources.Program.MailSender_MailSendFailed)]);
            }
            finally
            {
                try { await _smtpClient!.DisconnectAsync(true, _cancellationToken); } catch { }
            }
        }
    }

    private bool EnqueueMailTask(string? userName, string? email, string? resetLink, MailType type,
        IStringLocalizer<Program> localizer, IOptionsSnapshot<GlobalConfig> options)
    {
        if (_smtpClient is null)
            return false;

        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(resetLink))
        {
            _logger.SystemLog(StaticLocalizer[nameof(Resources.Program.MailSender_InvalidRequest)],
                TaskStatus.Failed);
            return false;
        }

        var content = new MailContent(userName, email, resetLink, type, localizer, options);

        _mailQueue.Enqueue(content);
        _resetEvent.Set();

        return true;
    }

    ~MailSender()
    {
        Dispose();
    }
}

/// <summary>
/// 邮件类型
/// </summary>
public enum MailType
{
    ConfirmEmail,
    ChangeEmail,
    ResetPassword
}

/// <summary>
/// 邮件内容
/// </summary>
public class MailContent(
    string userName,
    string email,
    string resetLink,
    MailType type,
    // DO NOT use IStringLocalizer<Program> after construction
    IStringLocalizer<Program> localizer,
    IOptionsSnapshot<GlobalConfig> globalConfig)
{
    /// <summary>
    /// 邮件模板
    /// </summary>
    public string Template { get; } = localizer[nameof(Resources.Program.MailSender_Template)];

    /// <summary>
    /// 邮件标题
    /// </summary>
    public string Title { get; } = type switch
    {
        MailType.ConfirmEmail => localizer[nameof(Resources.Program.MailSender_VerifyEmailTitle)],
        MailType.ChangeEmail => localizer[nameof(Resources.Program.MailSender_ChangeEmailTitle)],
        MailType.ResetPassword => localizer[nameof(Resources.Program.MailSender_ResetPasswordTitle)],
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>
    /// 邮件信息
    /// </summary>
    public string Information { get; } = type switch
    {
        MailType.ConfirmEmail => localizer[nameof(Resources.Program.MailSender_VerifyEmailContent), email],
        MailType.ChangeEmail => localizer[nameof(Resources.Program.MailSender_ChangeEmailContent)],
        MailType.ResetPassword => localizer[nameof(Resources.Program.MailSender_ResetPasswordContent)],
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>
    /// 邮件按钮显示内容
    /// </summary>
    public string ButtonMessage { get; } = type switch
    {
        MailType.ConfirmEmail => localizer[nameof(Resources.Program.MailSender_VerifyEmailButton)],
        MailType.ChangeEmail => localizer[nameof(Resources.Program.MailSender_ChangeEmailButton)],
        MailType.ResetPassword => localizer[nameof(Resources.Program.MailSender_ResetPasswordButton)],
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>
    /// 用户名
    /// </summary>
    public string UserName { get; } = userName;

    /// <summary>
    /// 用户邮箱
    /// </summary>
    public string Email { get; } = email;

    /// <summary>
    /// 邮件链接
    /// </summary>
    public string Url { get; } = resetLink;

    /// <summary>
    /// 发信时间
    /// </summary>
    public string Time { get; } = DateTimeOffset.UtcNow.ToString("u");

    /// <summary>
    /// 平台名称
    /// </summary>
    public string Platform { get; } = globalConfig.Value.Platform;
}
