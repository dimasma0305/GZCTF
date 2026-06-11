namespace GZCTF.Models.Request.Shared;

public class ClientFlagContext
{
    /// <summary>
    /// Close time of the challenge instance
    /// </summary>
    public DateTimeOffset? CloseTime { get; set; }

    /// <summary>
    /// Connection method of the challenge instance
    /// </summary>
    public string? InstanceEntry { get; set; }

    /// <summary>
    /// Whether this challenge serves a single container shared by all teams. When true the
    /// connection is read-only for players (only an admin can stop it).
    /// </summary>
    public bool IsSharedInstance { get; set; }

    /// <summary>
    /// Attachment URL
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Attachment file size
    /// </summary>
    public long? FileSize { get; set; }
}
