using System.ComponentModel.DataAnnotations;
using GZCTF.Utils;

namespace GZCTF.Models.Request.Edit;

/// <summary>
/// Body for <c>POST /api/Edit/Games/{id}/Challenges/ImportFromGitHub</c>.
/// </summary>
public sealed class ImportFromGitHubModel
{
    [Required]
    [MaxLength(Limits.UrlLength)]
    public string RepoUrl { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? Ref { get; set; }

    [MaxLength(512)]
    public string? Subpath { get; set; }

    /// <summary>
    /// Optional GitHub access token for private repos. Only honoured when
    /// the caller is admin / game-admin; user submissions ignore this
    /// field. Used in-flight only — never stored.
    /// </summary>
    [MaxLength(1024)]
    public string? GitHubToken { get; set; }
}

/// <summary>
/// Body for <c>POST .../Reject</c>. Admin-supplied free-form note.
/// </summary>
public sealed class RejectChallengeModel
{
    [MaxLength(Limits.MaxUserDataLength)]
    public string? Note { get; set; }
}
