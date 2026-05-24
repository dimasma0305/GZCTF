using GZCTF.Extensions;
using GZCTF.Middlewares;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Game;
using GZCTF.Models.Response.Admin;
using GZCTF.Services;
using GZCTF.Storage.Interface;
using GZCTF.Utils;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace GZCTF.Controllers;

/// <summary>
/// Player-facing Attack &amp; Defense endpoints. Kept separate from
/// <see cref="GameController"/> to avoid bloating that file further.
/// All endpoints validate the caller is a member of an accepted
/// Participation in the target game.
/// </summary>
[ApiController]
[Route("api/Game/{id:int}/Ad")]
[Produces("application/json")]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
public class AdGameController(
    AppDbContext db,
    UserManager<UserInfo> userManager,
    AdContainerManager adContainerManager,
    IBlobStorage blobStorage,
    IStringLocalizer<Program> localizer,
    ILogger<AdGameController> logger) : ControllerBase
{
    /// <summary>
    /// Submit a captured flag from another team's service.
    /// </summary>
    [RequireUser]
    [HttpPost("Submit")]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Submit))]
    [ProducesResponseType(typeof(AdSubmitResultModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Submit(int id, [FromBody] AdSubmitModel model, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return Unauthorized();

        // Caller's participation in the target game.
        var attackerPart = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);

        if (attackerPart is null)
            return Forbid();

        // Current round.
        var currentRound = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);

        if (currentRound is null)
            return Ok(new AdSubmitResultModel
            {
                Status = "not_started",
                Message = "No A&D round has started yet for this game"
            });

        var submittedFlag = model.Flag.Trim();
        if (string.IsNullOrEmpty(submittedFlag))
            return Ok(new AdSubmitResultModel { Status = "wrong", Message = "empty flag" });

        // Find the AdFlag by exact flag string. Indexed on Flag column.
        var adFlag = await db.AdFlags
            .Include(f => f.AdTeamService)
            .FirstOrDefaultAsync(f => f.Flag == submittedFlag, token);

        if (adFlag is null)
            return Ok(new AdSubmitResultModel { Status = "wrong", Message = "flag not recognized" });

        // Same-game guard.
        var victimPart = await db.Participations
            .FirstOrDefaultAsync(p => p.Id == adFlag.AdTeamService.ParticipationId, token);
        if (victimPart is null || victimPart.GameId != id)
            return Ok(new AdSubmitResultModel { Status = "wrong", Message = "flag from another game" });

        // Self-attack guard.
        if (victimPart.Id == attackerPart.Id)
            return Ok(new AdSubmitResultModel { Status = "self_attack", Message = "cannot submit your own flag" });

        // Flag-lifetime guard.
        var challenge = await db.GameChallenges.FirstAsync(c => c.Id == adFlag.AdTeamService.ChallengeId, token);
        var lifetimeTicks = challenge.AdFlagLifetimeTicks ?? 5;
        if (adFlag.PlantedAtRound < currentRound.Number - lifetimeTicks + 1)
            return Ok(new AdSubmitResultModel
            {
                Status = "expired",
                FlagPlantedAtRound = adFlag.PlantedAtRound,
                Message = $"flag was planted {currentRound.Number - adFlag.PlantedAtRound} ticks ago (lifetime {lifetimeTicks})"
            });

        // Duplicate guard — unique index (AttackerParticipationId, AdFlagId) will
        // also catch this; pre-check for a friendly response.
        var alreadySubmitted = await db.AdAttacks.AnyAsync(
            a => a.AttackerParticipationId == attackerPart.Id && a.AdFlagId == adFlag.Id, token);
        if (alreadySubmitted)
            return Ok(new AdSubmitResultModel
            {
                Status = "duplicate",
                FlagPlantedAtRound = adFlag.PlantedAtRound,
                Message = "you've already submitted this flag"
            });

        // FAUST-ish scoring: base 10 points / sqrt(distinct capturers including this one).
        var priorCapturers = await db.AdAttacks.CountAsync(a => a.AdFlagId == adFlag.Id, token);
        var points = 10.0 / Math.Sqrt(priorCapturers + 1);

        var attack = new AdAttack
        {
            AdFlagId = adFlag.Id,
            AttackerParticipationId = attackerPart.Id,
            VictimParticipationId = victimPart.Id,
            ChallengeId = adFlag.AdTeamService.ChallengeId,
            SubmittedAtRound = currentRound.Number,
            Points = points
        };

        try
        {
            await db.AdAttacks.AddAsync(attack, token);
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateException)
        {
            // Hit the unique constraint via race — treat as duplicate.
            return Ok(new AdSubmitResultModel
            {
                Status = "duplicate",
                FlagPlantedAtRound = adFlag.PlantedAtRound,
                Message = "already submitted"
            });
        }

        logger.SystemLog(
            $"A&D attack landed: attacker={attackerPart.Id} victim={victimPart.Id} chal={adFlag.AdTeamService.ChallengeId} round={currentRound.Number} pts={points:F2}",
            TaskStatus.Success, LogLevel.Information);

        return Ok(new AdSubmitResultModel
        {
            Status = "accepted",
            Points = points,
            FlagPlantedAtRound = adFlag.PlantedAtRound
        });
    }

    /// <summary>
    /// Self-reset one of your team's A&amp;D containers to baseline.
    /// </summary>
    [RequireUser]
    [HttpPost("Services/{adTeamServiceId:int}/Reset")]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Concurrency))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ResetService(int id, int adTeamServiceId, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var ts = await db.AdTeamServices
            .Include(t => t.Participation)
            .Include(t => t.Challenge)
            .FirstOrDefaultAsync(t => t.Id == adTeamServiceId, token);

        if (ts is null || ts.Participation.GameId != id)
            return NotFound();

        // Caller must be a member of the team that owns this service.
        var isMember = await db.Participations
            .AnyAsync(p => p.Id == ts.ParticipationId && p.Members.Any(m => m.UserId == user.Id), token);
        if (!isMember) return Forbid();

        if (!ts.Challenge.AdAllowSelfReset)
            return BadRequest(new RequestResponse("Self-reset is disabled for this challenge by the operator"));

        // Cooldown check.
        var cooldownMinutes = ts.Challenge.AdResetCooldownMinutes ?? 5;
        if (ts.LastResetAt is { } last)
        {
            var elapsed = DateTimeOffset.UtcNow - last;
            if (elapsed < TimeSpan.FromMinutes(cooldownMinutes))
            {
                var retryAfter = (int)(TimeSpan.FromMinutes(cooldownMinutes) - elapsed).TotalSeconds;
                Response.Headers.RetryAfter = retryAfter.ToString();
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    new RequestResponse(
                        $"Cooldown active; try again in {retryAfter}s",
                        StatusCodes.Status429TooManyRequests));
            }
        }

        var ok = await adContainerManager.RestartContainerAsync(ts.Id, token);
        if (!ok)
            return BadRequest(new RequestResponse("Container restart failed; check logs"));

        logger.SystemLog($"A&D self-reset: team={ts.ParticipationId} challenge={ts.ChallengeId} by user={user.Id}",
            TaskStatus.Success, LogLevel.Information);

        return Ok();
    }

    /// <summary>
    /// Get the player view of A&amp;D state for their team in this game.
    /// </summary>
    [RequireUser]
    [HttpGet("State")]
    [ProducesResponseType(typeof(AdStateModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> State(int id, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var part = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);

        if (part is null) return Forbid();

        var currentRound = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);

        var services = await db.AdTeamServices
            .Where(t => t.ParticipationId == part.Id)
            .Include(t => t.Challenge)
            .Include(t => t.Container)
            .ToListAsync(token);

        var serviceIds = services.Select(s => s.Id).ToList();
        var currentFlags = currentRound is null ? new Dictionary<int, string>() :
            await db.AdFlags
                .Where(f => f.AdRoundId == currentRound.Id && serviceIds.Contains(f.AdTeamServiceId))
                .ToDictionaryAsync(f => f.AdTeamServiceId, f => f.Flag, token);

        var lastChecks = serviceIds.Count == 0 ? [] :
            await db.AdCheckResults
                .Where(c => serviceIds.Contains(c.AdTeamServiceId))
                .GroupBy(c => c.AdTeamServiceId)
                .Select(g => g.OrderByDescending(c => c.CheckedAt).First())
                .ToListAsync(token);

        var lastChecksByService = lastChecks.ToDictionary(c => c.AdTeamServiceId);

        var serviceModels = services.Select(s =>
        {
            var cooldownMinutes = s.Challenge.AdResetCooldownMinutes ?? 5;
            var cooldownRemaining = s.LastResetAt is { } last
                ? Math.Max(0, (int)(TimeSpan.FromMinutes(cooldownMinutes) - (DateTimeOffset.UtcNow - last)).TotalSeconds)
                : 0;

            return new AdTeamServiceStateModel
            {
                AdTeamServiceId = s.Id,
                ChallengeId = s.ChallengeId,
                ChallengeTitle = s.Challenge.Title,
                ContainerIp = s.Container?.IP,
                ContainerPort = s.Container?.Port,
                CurrentFlag = currentFlags.GetValueOrDefault(s.Id),
                LastCheckStatus = lastChecksByService.GetValueOrDefault(s.Id)?.Status.ToString(),
                LastResetAt = s.LastResetAt,
                CanReset = s.Challenge.AdAllowSelfReset && cooldownRemaining == 0,
                ResetCooldownSecondsRemaining = cooldownRemaining > 0 ? cooldownRemaining : null
            };
        }).ToList();

        return Ok(new AdStateModel
        {
            CurrentRound = currentRound?.Number ?? 0,
            RoundStartedAt = currentRound?.StartedAt,
            RoundEndsAt = currentRound?.EndsAt,
            Services = serviceModels
        });
    }

    /// <summary>
    /// Get the A&amp;D scoreboard for this game — independent of the jeopardy
    /// scoreboard. Public; respects the game's hidden flag.
    /// </summary>
    [HttpGet("Scoreboard")]
    [ProducesResponseType(typeof(AdScoreboardModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Scoreboard(int id, CancellationToken token)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == id, token);
        if (game is null || game.Hidden) return NotFound();

        var hasAd = await db.GameChallenges.AnyAsync(
            c => c.GameId == id && c.Type == ChallengeType.AttackDefense, token);
        if (!hasAd) return NotFound();

        var latestRound = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderByDescending(r => r.Number)
            .Select(r => r.Number)
            .FirstOrDefaultAsync(token);

        var teams = await db.Participations
            .Where(p => p.GameId == id && p.Status == ParticipationStatus.Accepted)
            .Include(p => p.Team)
            .Include(p => p.Division)
            .ToListAsync(token);

        var partIds = teams.Select(p => p.Id).ToList();

        // Attack stats per attacker.
        var attackAgg = await db.AdAttacks
            .Where(a => partIds.Contains(a.AttackerParticipationId))
            .GroupBy(a => a.AttackerParticipationId)
            .Select(g => new
            {
                PartId = g.Key,
                Points = g.Sum(a => a.Points),
                Count = g.Count()
            })
            .ToDictionaryAsync(g => g.PartId, token);

        // Defense loss per victim: each distinct (AdFlagId) the victim was
        // tagged for counts once (DB pre-aggregates).
        var defenseAgg = await db.AdAttacks
            .Where(a => partIds.Contains(a.VictimParticipationId))
            .GroupBy(a => a.VictimParticipationId)
            .Select(g => new { PartId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.PartId, token);

        // SLA: count of Ok checks vs total checks per team.
        var checks = await db.AdCheckResults
            .Where(c => db.AdTeamServices
                .Where(ts => ts.Id == c.AdTeamServiceId)
                .Any(ts => partIds.Contains(ts.ParticipationId)))
            .Join(db.AdTeamServices,
                c => c.AdTeamServiceId, ts => ts.Id,
                (c, ts) => new { ts.ParticipationId, c.Status })
            .GroupBy(x => x.ParticipationId)
            .Select(g => new
            {
                PartId = g.Key,
                Ok = g.Count(x => x.Status == AdCheckStatus.Ok),
                Total = g.Count()
            })
            .ToDictionaryAsync(g => g.PartId, token);

        const double slaScale = 10.0;
        const double defensePenaltyScale = 2.0;

        var rows = teams.Select(p =>
        {
            var atk = attackAgg.GetValueOrDefault(p.Id);
            var def = defenseAgg.GetValueOrDefault(p.Id);
            var sla = checks.GetValueOrDefault(p.Id);

            var attackPoints = atk?.Points ?? 0;
            var timesCaptured = def?.Count ?? 0;
            var defenseLoss = Math.Pow(timesCaptured, 0.75) * defensePenaltyScale;
            var slaFraction = sla is { Total: > 0 } ? (double)sla.Ok / sla.Total : 0;
            var slaPoints = slaFraction * slaScale * Math.Max(1, latestRound);

            return new AdTeamScoreRow
            {
                ParticipationId = p.Id,
                TeamId = p.TeamId,
                TeamName = p.Team.Name,
                Division = p.Division?.Name,
                AttackPoints = attackPoints,
                DefenseLoss = defenseLoss,
                SlaPoints = slaPoints,
                Total = attackPoints - defenseLoss + slaPoints,
                TimesCaptured = timesCaptured,
                FlagsCaptured = atk?.Count ?? 0
            };
        })
        .OrderByDescending(r => r.Total)
        .ToList();

        for (int i = 0; i < rows.Count; i++)
            rows[i].Rank = i + 1;

        return Ok(new AdScoreboardModel
        {
            LatestRound = latestRound,
            Teams = rows
        });
    }

    /// <summary>
    /// Download the post-game container snapshot tarball for one of the
    /// caller's team services. Available only after game end + if the
    /// challenge has AdAllowSnapshotDownload=true + a snapshot was actually
    /// taken (Docker provider only for v1).
    /// </summary>
    [RequireUser]
    [HttpGet("Services/{adTeamServiceId:int}/Snapshot")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadSnapshot(int id, int adTeamServiceId, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var ts = await db.AdTeamServices
            .Include(t => t.Participation)
            .FirstOrDefaultAsync(t => t.Id == adTeamServiceId, token);

        if (ts is null || ts.Participation.GameId != id) return NotFound();

        var isMember = await db.Participations
            .AnyAsync(p => p.Id == ts.ParticipationId && p.Members.Any(m => m.UserId == user.Id), token);
        if (!isMember) return Forbid();

        if (string.IsNullOrEmpty(ts.SnapshotBlobKey))
            return NotFound(new RequestResponse("Snapshot not available (game still running, snapshot disabled, or snapshot failed)"));

        if (!await blobStorage.ExistsAsync(ts.SnapshotBlobKey, token))
        {
            logger.SystemLog($"A&D snapshot blob missing: key={ts.SnapshotBlobKey}",
                TaskStatus.Failed, LogLevel.Warning);
            return NotFound(new RequestResponse("Snapshot blob is missing — may have been retained-out"));
        }

        var stream = await blobStorage.OpenReadAsync(ts.SnapshotBlobKey, token);
        var filename = $"ad-snapshot-team{ts.ParticipationId}-challenge{ts.ChallengeId}.tar.gz";
        return File(stream, "application/gzip", filename);
    }
}
