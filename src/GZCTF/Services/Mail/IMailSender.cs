using GZCTF.Models.Internal;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace GZCTF.Services.Mail;

/// <summary>Per-recipient outcome of one credential email in a batch send.</summary>
/// <param name="Email">Recipient address.</param>
/// <param name="UserName">Recipient username (echoed so the caller can re-target a resend).</param>
/// <param name="Sent">True if the message was accepted by the SMTP server.</param>
/// <param name="Error">Short failure reason when <paramref name="Sent"/> is false; null on success.</param>
public record CredentialSendResult(string Email, string UserName, bool Sent, string? Error);

/// <summary>Aggregate result of <see cref="IMailSender.SendCredentialsBatch"/>.</summary>
/// <param name="Sent">Count delivered.</param>
/// <param name="Failed">Count not delivered (including recipients with no matching user).</param>
/// <param name="Results">Per-recipient outcomes, in input order.</param>
public record CredentialsBatchResult(int Sent, int Failed, IReadOnlyList<CredentialSendResult> Results);

public interface IMailSender
{
    /// <summary>
    /// 发送带邮件内容
    /// </summary>
    /// <param name="content">邮件内容</param>
    public Task SendMailContent(MailContent content);

    /// <summary>
    /// 发送新用户验证URL
    /// </summary>
    /// <param name="userName">用户名</param>
    /// <param name="email">用户新注册的Email</param>
    /// <param name="confirmLink">确认链接</param>
    /// <param name="localizer">本地化</param>
    /// <param name="options">全局配置</param>
    public bool SendConfirmEmailUrl(string? userName, string? email, string? confirmLink,
        IStringLocalizer<Program> localizer, IOptionsSnapshot<GlobalConfig> options);

    /// <summary>
    /// 发送邮箱重置邮件
    /// </summary>
    /// <param name="userName">用户名</param>
    /// <param name="email">用户的电子邮件</param>
    /// <param name="resetLink">重置链接</param>
    /// <param name="localizer">本地化</param>
    /// <param name="options">全局配置</param>
    public bool SendChangeEmailUrl(string? userName, string? email, string? resetLink,
        IStringLocalizer<Program> localizer, IOptionsSnapshot<GlobalConfig> options);

    /// <summary>
    /// 发送密码重置邮件
    /// </summary>
    /// <param name="userName">用户名</param>
    /// <param name="email">用户的电子邮件</param>
    /// <param name="resetLink">重置链接</param>
    /// <param name="localizer">本地化</param>
    /// <param name="options">全局配置</param>
    public bool SendResetPasswordUrl(string? userName, string? email, string? resetLink,
        IStringLocalizer<Program> localizer, IOptionsSnapshot<GlobalConfig> options);

    /// <summary>
    /// Batch-send "set your password" emails. Each email contains a one-time reset
    /// link — no password is transmitted. The SMTP session is recycled periodically
    /// (and on session-level errors) so a server's per-session message cap doesn't
    /// silently drop the tail of a large batch. Returns a per-recipient outcome so
    /// the caller can report — and resend — exactly the ones that failed.
    /// </summary>
    public Task<CredentialsBatchResult> SendCredentialsBatch(
        IEnumerable<(string UserName, string Email, string ResetLink)> items,
        string loginUrl,
        IStringLocalizer<Program> localizer,
        IOptionsSnapshot<GlobalConfig> options,
        CancellationToken token = default);

    /// <summary>
    /// Smoke-test the supplied SMTP configuration by sending a single
    /// fixed-content message to <paramref name="recipient"/>. Used by
    /// the "Send test" button in /admin/settings. Does not touch the
    /// singleton's persisted SmtpClient — every call gets its own
    /// short-lived connection so an in-progress test doesn't break the
    /// live mail queue. <paramref name="passwordPlain"/> is the
    /// already-decrypted SMTP password (the controller resolves it
    /// from the request body or the stored DB value).
    /// </summary>
    public Task<(bool Ok, string? Error)> TestSendAsync(
        EmailConfig config, string passwordPlain, string recipient, CancellationToken token = default);
}
