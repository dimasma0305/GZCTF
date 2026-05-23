using System.ComponentModel.DataAnnotations;
using GZCTF.Models.Internal;

namespace GZCTF.Models.Request.Admin;

/// <summary>
/// Body for the POST /api/admin/email/test endpoint. Lets the operator
/// verify SMTP settings from /admin/settings without persisting them.
/// </summary>
public class EmailTestModel
{
    /// <summary>
    /// SMTP config to test. When <see cref="EmailConfig.Password"/> is
    /// empty the controller falls back to the currently-stored value
    /// (same preserve-on-blank shape as the Save flow) so the operator
    /// can test without re-typing a configured password.
    /// </summary>
    [Required]
    public EmailConfig Config { get; set; } = new();

    /// <summary>
    /// Destination mailbox for the test message.
    /// </summary>
    [Required]
    [EmailAddress]
    public string Recipient { get; set; } = string.Empty;
}
