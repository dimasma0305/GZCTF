using GZCTF.Extensions;
using GZCTF.Middlewares;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Game;
using GZCTF.Services;
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
}
