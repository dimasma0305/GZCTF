using System.ComponentModel.DataAnnotations;
using GZCTF.Models.Internal;

namespace GZCTF.Models.Request.Admin;

/// <summary>
/// Body for POST /api/admin/captcha/test. Lets the operator verify
/// captcha settings from /admin/settings without persisting them.
/// </summary>
public class CaptchaTestModel
{
    /// <summary>
    /// Captcha config to test. When
    /// <see cref="CaptchaConfig.SecretKey"/> is empty the controller
    /// falls back to the currently-stored value (same preserve-on-blank
    /// shape as the Save flow) so the operator can test without
    /// re-typing a configured secret.
    /// </summary>
    [Required]
    public CaptchaConfig Config { get; set; } = new();
}
