using System.Text;
using GZCTF.Extensions;
using GZCTF.Middlewares;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Internal;
using GZCTF.Models.Request.Admin;
using GZCTF.Repositories.Interface;
using GZCTF.Services;
using GZCTF.Services.Container.Manager;
using GZCTF.Storage.Interface;
using GZCTF.Utils;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace GZCTF.Controllers;

/// <summary>
/// Admin / GameAdmin Attack &amp; Defense ops endpoints — the operator console
/// for running an A&amp;D event live. All under /api/edit/games/{id}/ad/*.
/// </summary>
[ApiController]
[Route("api/edit/games/{id:int}/ad")]
[Produces("application/json")]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
public class AdAdminController(
    AppDbContext db,
    AdContainerManager adContainerManager,
    AdRoundService adRoundService,
    IBlobStorage blobStorage,
    UserManager<UserInfo> userManager,
    IContainerManager containerService,
    IContainerRepository containerRepository,
    IStringLocalizer<Program> localizer,
    ILogger<AdAdminController> logger) : ControllerBase
{
    private static int? CountChanges(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonDocument.Parse(json).RootElement.GetArrayLength(); }
        catch { return null; }
    }

    /// <summary>
    /// Manually advance to the next A&amp;D round, planting fresh flags for
    /// every (team, A&amp;D challenge). Same code path the
    /// <see cref="AdRoundScheduler"/> background service uses for auto
    /// tick-end advance — exposed here for the operator's "Force advance"
    /// button when they want to skip ahead or seed round 1 before warmup
    /// elapses.
    /// </summary>
    [RequireGameAdmin]
    [HttpPost("AdvanceRound")]
    [ProducesResponseType(typeof(AdAdvanceRoundResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> AdvanceRound(int id, CancellationToken token)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == id, token);
        if (game is null) return NotFound();

        var result = await adRoundService.AdvanceAsync(id, token);
        if (result is null)
            return BadRequest(new RequestResponse("Game has no enabled A&D challenges"));

        return Ok(new AdAdvanceRoundResult
        {
            RoundNumber = result.Round.Number,
            FlagsPlanted = result.FlagsPlanted,
            StartedAt = result.Round.StartedAt,
            EndsAt = result.Round.EndsAt
        });
    }

    /// <summary>
    /// Operator view of the live A&amp;D game state.
    /// </summary>
    [RequireGameAdmin]
    [HttpGet("State")]
    [ProducesResponseType(typeof(AdGameStateModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> State(int id, CancellationToken token)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == id, token);
        if (game is null) return NotFound();

        var adChallenges = await db.GameChallenges
            .Where(c => c.GameId == id && c.Type == ChallengeType.AttackDefense)
            .ToListAsync(token);

        var currentRound = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);

        var participations = await db.Participations
            .Where(p => p.GameId == id && p.Status == ParticipationStatus.Accepted)
            .Include(p => p.Team)
            .ToListAsync(token);

        var partIds = participations.Select(p => p.Id).ToList();

        var services = await db.AdTeamServices
            .Where(ts => partIds.Contains(ts.ParticipationId))
            .Include(ts => ts.Container)
            .ToListAsync(token);

        var serviceIds = services.Select(s => s.Id).ToList();

        var lastChecks = serviceIds.Count == 0 ? [] :
            await db.AdCheckResults
                .Where(c => serviceIds.Contains(c.AdTeamServiceId))
                .GroupBy(c => c.AdTeamServiceId)
                .Select(g => g.OrderByDescending(c => c.CheckedAt).First())
                .ToListAsync(token);
        var lastChecksByService = lastChecks.ToDictionary(c => c.AdTeamServiceId);

        var currentFlags = currentRound is null ? new Dictionary<int, string>() :
            await db.AdFlags
                .Where(f => f.AdRoundId == currentRound.Id && serviceIds.Contains(f.AdTeamServiceId))
                .ToDictionaryAsync(f => f.AdTeamServiceId, f => f.Flag, token);

        var rows = participations.Select(p => new AdTeamRowModel
        {
            ParticipationId = p.Id,
            TeamName = p.Team.Name,
            Services = services.Where(s => s.ParticipationId == p.Id).Select(s => new AdTeamCellModel
            {
                AdTeamServiceId = s.Id,
                ChallengeId = s.ChallengeId,
                ContainerIp = s.Container?.IP,
                ContainerPort = s.Container?.Port,
                ContainerGuid = s.ContainerId,
                LastCheckStatus = lastChecksByService.GetValueOrDefault(s.Id)?.Status.ToString(),
                LastCheckId = lastChecksByService.GetValueOrDefault(s.Id)?.Id,
                CurrentFlag = currentFlags.GetValueOrDefault(s.Id),
                SnapshotAvailable = !string.IsNullOrEmpty(s.SnapshotBlobKey),
                ChangedFileCount = CountChanges(s.SnapshotChanges)
            }).ToList()
        }).ToList();

        // Tick + flag lifetime are game-wide now — same value for every row.
        var gameTickSeconds = game.AdTickSeconds ?? 60;
        var gameFlagLifetimeTicks = game.AdFlagLifetimeTicks ?? 5;
        var challengeStates = adChallenges.Select(c => new AdChallengeStateModel
        {
            ChallengeId = c.Id,
            Title = c.Title,
            IsEnabled = c.IsEnabled,
            TickSeconds = gameTickSeconds,
            FlagLifetimeTicks = gameFlagLifetimeTicks,
            TeamsWithLiveContainer = services.Count(s => s.ChallengeId == c.Id && s.ContainerId is not null)
        }).ToList();

        return Ok(new AdGameStateModel
        {
            CurrentRound = currentRound?.Number,
            RoundStartedAt = currentRound?.StartedAt,
            RoundEndsAt = currentRound?.EndsAt,
            ScoringPaused = game.AdScoringPaused,
            ScoringPausedAt = game.AdScoringPausedAt,
            Challenges = challengeStates,
            Teams = rows
        });
    }

    /// <summary>
    /// Toggle a single A&amp;D challenge enabled/disabled mid-event. Disabled =
    /// checker stops, no flag rotation, no SLA scoring; containers stay up so
    /// teams can still patch.
    /// </summary>
    [RequireGameAdmin]
    [HttpPost("Challenges/{challengeId:int}/Toggle")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ToggleChallenge(int id, int challengeId, CancellationToken token)
    {
        var c = await db.GameChallenges
            .FirstOrDefaultAsync(c => c.Id == challengeId && c.GameId == id, token);
        if (c is null) return NotFound();
        if (c.Type != ChallengeType.AttackDefense)
            return BadRequest(new RequestResponse("Not an A&D challenge"));

        c.IsEnabled = !c.IsEnabled;
        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D challenge toggled: game={id} challenge={challengeId} enabled={c.IsEnabled}",
            TaskStatus.Success, LogLevel.Information);

        return Ok(new { c.IsEnabled });
    }

    /// <summary>
    /// Pause / resume A&amp;D scoring for the whole game. While paused the round
    /// scheduler stops advancing (no new flags) and the checker stops recording
    /// (no SLA accrual); containers stay up. Toggles
    /// <see cref="Game.AdScoringPaused"/>.
    /// </summary>
    [RequireGameAdmin]
    [HttpPost("ScoringPause")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ToggleScoringPause(int id, CancellationToken token)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == id, token);
        if (game is null) return NotFound();

        var now = DateTimeOffset.UtcNow;

        if (!game.AdScoringPaused)
        {
            // Pausing — remember when, so the UI freezes the timer and resume
            // can give the round back its remaining time.
            game.AdScoringPaused = true;
            game.AdScoringPausedAt = now;
        }
        else
        {
            // Resuming — shift the current round forward by the paused duration
            // so it doesn't instantly expire (and skip a round) on resume; the
            // remaining time at pause is preserved.
            if (game.AdScoringPausedAt is { } pausedAt)
            {
                var pausedFor = now - pausedAt;
                if (pausedFor > TimeSpan.Zero)
                {
                    var current = await db.AdRounds
                        .Where(r => r.GameId == id)
                        .OrderByDescending(r => r.Number)
                        .FirstOrDefaultAsync(token);
                    if (current is not null)
                    {
                        current.StartedAt += pausedFor;
                        current.EndsAt += pausedFor;
                    }
                }
            }
            game.AdScoringPaused = false;
            game.AdScoringPausedAt = null;
        }

        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D scoring {(game.AdScoringPaused ? "paused" : "resumed")}: game={id}",
            TaskStatus.Success, LogLevel.Information);

        return Ok(new { scoringPaused = game.AdScoringPaused });
    }

    /// <summary>
    /// Operator force-restart of a specific team's A&amp;D container. Bypasses
    /// the player-facing cooldown — for when a team's box is wedged and they
    /// can't recover themselves.
    /// </summary>
    [RequireGameAdmin]
    [HttpPost("Services/{adTeamServiceId:int}/Restart")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForceRestart(int id, int adTeamServiceId, CancellationToken token)
    {
        var ts = await db.AdTeamServices
            .Include(t => t.Participation)
            .FirstOrDefaultAsync(t => t.Id == adTeamServiceId, token);
        if (ts is null || ts.Participation.GameId != id) return NotFound();

        var ok = await adContainerManager.RestartContainerAsync(adTeamServiceId, token);
        if (!ok) return BadRequest(new RequestResponse("Restart failed; check logs"));

        logger.SystemLog($"A&D force-restart by admin: team={ts.ParticipationId} challenge={ts.ChallengeId}",
            TaskStatus.Success, LogLevel.Information);
        return Ok();
    }

    /// <summary>
    /// Override a recorded AdCheckResult — judge call when a check was wrong
    /// (e.g. transient network glitch made a healthy service look Offline).
    /// </summary>
    [RequireAdmin]
    [HttpPost("Checks/{checkId:int}/Override")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> OverrideCheck(int id, int checkId,
        [FromBody] AdOverrideCheckModel model, CancellationToken token)
    {
        var check = await db.AdCheckResults
            .Include(c => c.AdRound)
            .FirstOrDefaultAsync(c => c.Id == checkId, token);
        if (check is null || check.AdRound.GameId != id) return NotFound();

        var previous = check.Status;
        check.Status = model.NewStatus;
        if (!string.IsNullOrEmpty(model.Note))
            check.ErrorMessage = $"[admin override: {previous} → {model.NewStatus}] {model.Note}";

        await db.SaveChangesAsync(token);
        logger.SystemLog(
            $"A&D check overridden: game={id} check={checkId} {previous} → {model.NewStatus}",
            TaskStatus.Success, LogLevel.Information);

        return Ok();
    }

    /// <summary>
    /// Ensure containers for the game now — useful right after accepting a
    /// late-join participation when you don't want to wait for the next
    /// reconcile tick.
    /// </summary>
    [RequireGameAdmin]
    [HttpPost("EnsureContainers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> EnsureContainers(int id, CancellationToken token)
    {
        await adContainerManager.EnsureContainersForGameAsync(id, token);
        return Ok();
    }

    /// <summary>
    /// Download any team's post-game container snapshot tarball (admin
    /// forensics — "what did they ship"). Unlike the player endpoint this
    /// isn't team-scoped: a game admin can pull any team's snapshot.
    /// </summary>
    [RequireGameAdmin]
    [HttpGet("Services/{adTeamServiceId:int}/Snapshot")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadSnapshot(int id, int adTeamServiceId, CancellationToken token)
    {
        var ts = await db.AdTeamServices
            .Include(t => t.Participation)
            .FirstOrDefaultAsync(t => t.Id == adTeamServiceId, token);
        if (ts is null || ts.Participation.GameId != id) return NotFound();

        if (string.IsNullOrEmpty(ts.SnapshotBlobKey))
            return NotFound(new RequestResponse("No snapshot for this team-service (game still running, snapshot disabled, or it failed)"));

        if (!await blobStorage.ExistsAsync(ts.SnapshotBlobKey, token))
            return NotFound(new RequestResponse("Snapshot blob is missing — may have been retained-out"));

        var stream = await blobStorage.OpenReadAsync(ts.SnapshotBlobKey, token);
        var filename = $"ad-snapshot-team{ts.ParticipationId}-challenge{ts.ChallengeId}.tar.gz";
        return File(stream, "application/gzip", filename);
    }

    /// <summary>
    /// The filesystem diff (docker diff) of a team's container vs the
    /// baseline image, captured at snapshot time — the admin "what did they
    /// change" view.
    /// </summary>
    [RequireGameAdmin]
    [HttpGet("Services/{adTeamServiceId:int}/Snapshot/Changes")]
    [ProducesResponseType(typeof(AdSnapshotChangesModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> SnapshotChanges(int id, int adTeamServiceId, CancellationToken token)
    {
        var ts = await db.AdTeamServices
            .Include(t => t.Participation)
            .FirstOrDefaultAsync(t => t.Id == adTeamServiceId, token);
        if (ts is null || ts.Participation.GameId != id) return NotFound();

        var model = new AdSnapshotChangesModel
        {
            SnapshotAvailable = !string.IsNullOrEmpty(ts.SnapshotBlobKey)
        };

        if (!string.IsNullOrEmpty(ts.SnapshotChanges))
        {
            // Post-game: the diff captured at snapshot time.
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(ts.SnapshotChanges);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    model.Changes.Add(new AdSnapshotChange
                    {
                        Path = el.GetProperty("p").GetString() ?? string.Empty,
                        Kind = el.TryGetProperty("k", out var k) ? k.GetInt32() : 0
                    });
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "A&D snapshot changes parse failed for service={Sid}", adTeamServiceId);
            }
        }
        else if (ts.ContainerId is not null)
        {
            // Mid-game: compute the diff live against the running container.
            var live = await adContainerManager.ComputeLiveChangesAsync(HttpContext.RequestServices, ts, token);
            if (live is not null)
            {
                model.Live = true;
                foreach (var (path, kind) in live)
                    model.Changes.Add(new AdSnapshotChange { Path = path, Kind = kind });
            }
        }

        return Ok(model);
    }

    /// <summary>
    /// Inspect ONE changed file: its current content (from the team's running
    /// container), the baseline content (from the challenge image), and a unified
    /// diff between them. Powers the AdOps "view file / view diff" drill-down.
    /// Content needs a live container; the baseline comes from the (immutable,
    /// cached) image regardless.
    /// </summary>
    [RequireGameAdmin]
    [HttpGet("Services/{adTeamServiceId:int}/File")]
    [ProducesResponseType(typeof(AdFileViewModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> File(int id, int adTeamServiceId, [FromQuery] string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Model_ValidationFailed)]));

        var ts = await db.AdTeamServices
            .Include(t => t.Participation)
            .Include(t => t.Challenge)
            .Include(t => t.Container)
            .FirstOrDefaultAsync(t => t.Id == adTeamServiceId, token);
        if (ts is null || ts.Participation.GameId != id) return NotFound();

        var sp = HttpContext.RequestServices;
        var current = await adContainerManager.ReadCurrentFileBytesAsync(sp, ts, path, token);
        var baseline = await adContainerManager.ReadBaselineFileBytesAsync(sp, ts.Challenge.ContainerImage ?? string.Empty, path, token);

        var model = new AdFileViewModel
        {
            Path = path,
            ContainerRunning = ts.ContainerId is not null,
            Current = ToBlob(current),
            Baseline = ToBlob(baseline)
        };

        if (model.Current is { Binary: false, Text: { } cur } && model.Baseline is { Binary: false, Text: { } bas })
            model.UnifiedDiff = BuildUnifiedDiff(bas, cur);

        logger.LogInformation(
            "AD File inspect: service={Sid} cid={Cid} path={Path} running={Run} current={Cur} baseline={Base} diff={Diff}",
            adTeamServiceId, ts.ContainerId, path, model.ContainerRunning,
            model.Current is null ? "null" : $"{model.Current.Size}b{(model.Current.Binary ? " bin" : "")}",
            model.Baseline is null ? "null" : $"{model.Baseline.Size}b",
            model.UnifiedDiff is null ? "null" : $"{model.UnifiedDiff.Length}c");

        return Ok(model);
    }

    /// <summary>
    /// Spawn a short-lived inspector container from the challenge's image so an
    /// admin can shell in and explore files when there's no running team
    /// container (e.g. post-game). It's a FRESH container off the image — baseline
    /// files, not the team's edits (those live in the running container). Reaped
    /// by <c>ExpectStopAt</c>; the DELETE counterpart tears it down on shell close.
    /// </summary>
    [RequireGameAdmin]
    [HttpPost("Services/{adTeamServiceId:int}/Inspector")]
    [ProducesResponseType(typeof(AdInspectorModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> SpawnInspector(int id, int adTeamServiceId, CancellationToken token)
    {
        var ts = await db.AdTeamServices
            .Include(t => t.Participation)
            .Include(t => t.Challenge)
            .FirstOrDefaultAsync(t => t.Id == adTeamServiceId, token);
        if (ts is null || ts.Participation.GameId != id) return NotFound();
        if (string.IsNullOrWhiteSpace(ts.Challenge.ContainerImage))
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Container_ConfigError)]));

        var user = await userManager.GetUserAsync(User);
        var container = await containerService.CreateContainerAsync(new ContainerConfig
        {
            TeamId = "inspector",
            UserId = user!.Id,
            ChallengeId = ts.ChallengeId,
            GameId = ts.Participation.GameId,
            Image = ts.Challenge.ContainerImage!,
            CPUCount = ts.Challenge.CPUCount ?? 1,
            MemoryLimit = ts.Challenge.MemoryLimit ?? 64,
            StorageLimit = ts.Challenge.StorageLimit ?? 256,
            NetworkMode = NetworkMode.Isolated, // no egress — inspection only
            ExposedPort = ts.Challenge.ExposePort ?? 80,
        }, token);

        if (container is null)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Container_CreationFailed)]));

        // Short TTL so a forgotten inspector is reaped even if the DELETE never fires.
        container.ExpectStopAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
        await db.Containers.AddAsync(container, token);
        await db.SaveChangesAsync(token);

        logger.Log(
            $"A&D inspector spawned: service={adTeamServiceId} image={ts.Challenge.ContainerImage} container={container.LogId}",
            user, TaskStatus.Success);

        return Ok(new AdInspectorModel { ContainerGuid = container.Id });
    }

    /// <summary>Destroy an inspector container (called when the admin closes the shell).</summary>
    [RequireGameAdmin]
    [HttpDelete("Services/{adTeamServiceId:int}/Inspector/{containerGuid:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DestroyInspector(int id, int adTeamServiceId, Guid containerGuid, CancellationToken token)
    {
        var container = await containerRepository.GetContainerById(containerGuid, token);
        if (container is not null)
            await containerRepository.DestroyContainer(container, token);
        return Ok();
    }

    private const int MaxDiffLines = 1500;

    private static AdFileBlob? ToBlob((byte[] Data, bool Truncated)? read)
    {
        if (read is not { } r) return null;
        var binary = LooksBinary(r.Data);
        var blob = new AdFileBlob { Size = r.Data.Length, Truncated = r.Truncated, Binary = binary };
        if (binary) blob.Base64 = Convert.ToBase64String(r.Data);
        else blob.Text = Encoding.UTF8.GetString(r.Data);
        return blob;
    }

    private static bool LooksBinary(byte[] data)
    {
        var n = Math.Min(data.Length, 8000);
        for (var i = 0; i < n; i++)
            if (data[i] == 0) return true;
        return false;
    }

    /// <summary>Line-based unified diff (baseline → current) via LCS. Returns null
    /// when either side exceeds <see cref="MaxDiffLines"/> (the UI then shows the
    /// two sides separately).</summary>
    private static string? BuildUnifiedDiff(string baseline, string current)
    {
        var a = baseline.Replace("\r\n", "\n").Split('\n');
        var b = current.Replace("\r\n", "\n").Split('\n');
        if (a.Length > MaxDiffLines || b.Length > MaxDiffLines)
            return null;

        int m = a.Length, n = b.Length;
        var lcs = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
            for (var j = n - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var sb = new StringBuilder();
        int x = 0, y = 0;
        while (x < m && y < n)
        {
            if (a[x] == b[y]) { sb.Append(' ').Append(a[x]).Append('\n'); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { sb.Append('-').Append(a[x]).Append('\n'); x++; }
            else { sb.Append('+').Append(b[y]).Append('\n'); y++; }
        }
        while (x < m) { sb.Append('-').Append(a[x]).Append('\n'); x++; }
        while (y < n) { sb.Append('+').Append(b[y]).Append('\n'); y++; }
        return sb.ToString();
    }
}
