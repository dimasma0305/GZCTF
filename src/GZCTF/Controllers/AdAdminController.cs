using GZCTF.Extensions;
using GZCTF.Middlewares;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Admin;
using GZCTF.Services;
using GZCTF.Storage.Interface;
using GZCTF.Utils;
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
        var gameTickSeconds = game.AdTickSeconds ?? 120;
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

        game.AdScoringPaused = !game.AdScoringPaused;
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

        return Ok(model);
    }
}
