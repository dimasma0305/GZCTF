using System.Security.Cryptography;
using GZCTF.Extensions;
using GZCTF.Middlewares;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Admin;
using GZCTF.Services;
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
    IStringLocalizer<Program> localizer,
    ILogger<AdAdminController> logger) : ControllerBase
{
    private const int FlagRandomBytes = 24;

    /// <summary>
    /// Manually advance to the next A&amp;D round, planting fresh flags for
    /// every (team, A&amp;D challenge). Used in Phase 1 (no auto-checker yet)
    /// AND by the operator's emergency "force advance" button.
    /// </summary>
    [RequireGameAdmin]
    [HttpPost("AdvanceRound")]
    [ProducesResponseType(typeof(AdAdvanceRoundResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> AdvanceRound(int id, CancellationToken token)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == id, token);
        if (game is null) return NotFound();

        var adChallenges = await db.GameChallenges
            .Where(c => c.GameId == id && c.Type == ChallengeType.AttackDefense && c.IsEnabled)
            .ToListAsync(token);

        if (adChallenges.Count == 0)
            return BadRequest(new RequestResponse("Game has no enabled A&D challenges"));

        var prev = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);

        var nextNumber = (prev?.Number ?? 0) + 1;

        // Pick the shortest tick across enabled A&D challenges so the round
        // window honors the most-aggressive checker.
        var tickSeconds = adChallenges.Min(c => c.AdTickSeconds ?? 120);

        var now = DateTimeOffset.UtcNow;
        var round = new AdRound
        {
            GameId = id,
            Number = nextNumber,
            StartedAt = now,
            EndsAt = now.AddSeconds(tickSeconds)
        };
        await db.AdRounds.AddAsync(round, token);
        await db.SaveChangesAsync(token);

        // Plant a fresh flag for every (team, challenge) with a live container.
        var services = await db.AdTeamServices
            .Where(ts => ts.Participation.GameId == id)
            .ToListAsync(token);

        var flagsPlanted = 0;
        foreach (var ts in services)
        {
            var bytes = new byte[FlagRandomBytes];
            RandomNumberGenerator.Fill(bytes);
            var payload = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '_').Replace('/', '-');
            var flag = $"flag{{{payload}}}";

            await db.AdFlags.AddAsync(new AdFlag
            {
                AdRoundId = round.Id,
                AdTeamServiceId = ts.Id,
                PlantedAtRound = nextNumber,
                Flag = flag,
                PlantedAt = now
            }, token);
            flagsPlanted++;
        }

        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D round advanced: game={id} round={nextNumber} flags_planted={flagsPlanted}",
            TaskStatus.Success, LogLevel.Information);

        return Ok(new AdAdvanceRoundResult
        {
            RoundNumber = round.Number,
            FlagsPlanted = flagsPlanted,
            StartedAt = round.StartedAt,
            EndsAt = round.EndsAt
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
                LastCheckStatus = lastChecksByService.GetValueOrDefault(s.Id)?.Status.ToString(),
                CurrentFlag = currentFlags.GetValueOrDefault(s.Id)
            }).ToList()
        }).ToList();

        var challengeStates = adChallenges.Select(c => new AdChallengeStateModel
        {
            ChallengeId = c.Id,
            Title = c.Title,
            IsEnabled = c.IsEnabled,
            TickSeconds = c.AdTickSeconds ?? 120,
            FlagLifetimeTicks = c.AdFlagLifetimeTicks ?? 5,
            TeamsWithLiveContainer = services.Count(s => s.ChallengeId == c.Id && s.ContainerId is not null)
        }).ToList();

        return Ok(new AdGameStateModel
        {
            CurrentRound = currentRound?.Number,
            RoundStartedAt = currentRound?.StartedAt,
            RoundEndsAt = currentRound?.EndsAt,
            ScoringPaused = false, // wired in Phase 2 when scheduler exists
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
}
