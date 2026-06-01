using GZCTF.Utils;

namespace GZCTF.Models.Response.Admin;

/// <summary>
/// Row returned by <c>GET /api/Edit/Games/{id}/PendingChallenges</c>.
/// Compact summary for the admin review queue.
/// </summary>
public sealed class PendingChallengeModel
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public ChallengeCategory Category { get; set; }
    public ChallengeType Type { get; set; }

    /// <summary>
    /// Either <see cref="ChallengeReviewStatus.Pending"/> or
    /// <see cref="ChallengeReviewStatus.Rejected"/>; Active rows are
    /// excluded server-side.
    /// </summary>
    public ChallengeReviewStatus ReviewStatus { get; set; }

    /// <summary>
    /// Admin-supplied note from a previous Reject. Null otherwise.
    /// </summary>
    public string? ReviewNote { get; set; }

    public DateTimeOffset? SubmittedAtUtc { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedByUserName { get; set; }
}
