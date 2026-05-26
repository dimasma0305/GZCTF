using GZCTF.Models.Data;
using GZCTF.Utils;

namespace GZCTF.Services;

/// <summary>Outcome of a single A&amp;D check (provider-agnostic).</summary>
public sealed record AdCheckOutcome(AdCheckStatus Status, string? ErrorMessage, string? SourceIp);

/// <summary>One target for the built-in TCP-reachability probe.</summary>
public sealed record AdBuiltinTarget(int ServiceId, string Ip, int Port);

/// <summary>
/// Runs one A&amp;D check against a team's running service and returns the
/// verdict the scheduler persists as an <see cref="AdCheckResult"/>.
/// Provider-specific: Docker spawns a checker container
/// (<see cref="AdCheckerExecutor"/>); Kubernetes spawns an ephemeral checker
/// Pod and reads the exit code from pod status (<see cref="K8sAdCheckRunner"/>).
/// </summary>
public interface IAdCheckRunner
{
    Task<AdCheckOutcome> RunAsync(
        AdTeamService ts,
        AdRound round,
        GameChallenge challenge,
        string? plantedFlag,
        CancellationToken token);

    /// <summary>
    /// Batch the built-in TCP-reachability probe (challenges with no custom
    /// <see cref="GameChallenge.AdCheckerImage"/>) — returns serviceId →
    /// <see cref="AdCheckStatus.Ok"/>/<see cref="AdCheckStatus.Offline"/>. Docker
    /// probes in-process (no container); Kubernetes uses a single prober Pod for
    /// the whole batch instead of one Pod per check, so checker cost is O(1) pods
    /// per tick rather than O(teams × challenges).
    /// </summary>
    Task<IReadOnlyDictionary<int, AdCheckStatus>> RunBuiltinBatchAsync(
        IReadOnlyList<AdBuiltinTarget> targets,
        CancellationToken token);
}

/// <summary>Shared enochecker3 exit-code → <see cref="AdCheckStatus"/> mapping
/// used by both the Docker and Kubernetes check runners.</summary>
public static class AdCheckMapping
{
    public static AdCheckStatus FromExitCode(int exitCode, bool useCustomChecker)
    {
        // Built-in TCP probe (no custom checker): 0 → Ok, anything else → Offline.
        if (!useCustomChecker)
            return exitCode == 0 ? AdCheckStatus.Ok : AdCheckStatus.Offline;

        // Custom checker (enochecker3 contract).
        return exitCode switch
        {
            0 => AdCheckStatus.Ok,
            1 => AdCheckStatus.Mumble,
            2 => AdCheckStatus.Offline,
            _ => AdCheckStatus.InternalError
        };
    }
}
