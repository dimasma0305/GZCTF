using GZCTF.Extensions;
using GZCTF.Middlewares;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Game;
using GZCTF.Models.Response.Admin;
using GZCTF.Services;
using GZCTF.Services.Config;
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
    IConfigService configService,
    IStringLocalizer<Program> localizer,
    ILogger<AdGameController> logger) : ControllerBase
{
    /// <summary>
    /// Submit one or more captured flags from other teams' services. Accepts
    /// session-cookie auth (for the web tooling) OR <c>Authorization: Bearer
    /// ad_...</c> for scripted exploits. Returns per-flag results in input
    /// order so callers can correlate.
    /// </summary>
    [HttpPost("Submit")]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Submit))]
    [ProducesResponseType(typeof(AdBatchSubmitResultModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitBatch(int id, [FromBody] AdBatchSubmitModel model, CancellationToken token)
    {
        if (!ModelState.IsValid)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Model_ValidationFailed)]));

        var attackerPart = await ResolveTeamApiTokenAsync(id, token)
                           ?? await ResolveUserParticipationAsync(id, token);

        if (attackerPart is null)
            return Unauthorized();

        var currentRound = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);

        if (currentRound is null)
        {
            var notStarted = model.Flags.Select(f => new AdSubmitResultModel
            {
                Flag = f,
                Status = "not_started",
                Message = "No A&D round has started yet for this game"
            }).ToList();

            return Ok(new AdBatchSubmitResultModel { Results = notStarted });
        }

        var results = new List<AdSubmitResultModel>(model.Flags.Count);
        double totalPoints = 0;
        var acceptedCount = 0;

        foreach (var raw in model.Flags)
        {
            var result = await ProcessSingleFlagAsync(attackerPart, currentRound, raw, token);
            results.Add(result);
            if (result.Status == "accepted")
            {
                acceptedCount++;
                totalPoints += result.Points ?? 0;
            }
        }

        return Ok(new AdBatchSubmitResultModel
        {
            AcceptedCount = acceptedCount,
            TotalPoints = totalPoints,
            Results = results
        });
    }

    private async Task<AdSubmitResultModel> ProcessSingleFlagAsync(
        Participation attackerPart, AdRound currentRound, string raw, CancellationToken token)
    {
        var submittedFlag = (raw ?? string.Empty).Trim();
        var result = new AdSubmitResultModel { Flag = submittedFlag };

        if (string.IsNullOrEmpty(submittedFlag))
        {
            result.Status = "wrong";
            result.Message = "empty flag";
            return result;
        }

        var adFlag = await db.AdFlags
            .Include(f => f.AdTeamService)
            .FirstOrDefaultAsync(f => f.Flag == submittedFlag, token);

        if (adFlag is null)
        {
            result.Status = "wrong";
            result.Message = "flag not recognized";
            return result;
        }

        var victimPart = await db.Participations
            .FirstOrDefaultAsync(p => p.Id == adFlag.AdTeamService.ParticipationId, token);
        if (victimPart is null || victimPart.GameId != attackerPart.GameId)
        {
            result.Status = "wrong";
            result.Message = "flag from another game";
            return result;
        }

        if (victimPart.Id == attackerPart.Id)
        {
            result.Status = "self_attack";
            result.Message = "cannot submit your own flag";
            return result;
        }

        var lifetimeTicks = await db.Games
            .Where(g => g.Id == currentRound.GameId)
            .Select(g => g.AdFlagLifetimeTicks)
            .FirstOrDefaultAsync(token) ?? 5;
        if (adFlag.PlantedAtRound < currentRound.Number - lifetimeTicks + 1)
        {
            result.Status = "expired";
            result.FlagPlantedAtRound = adFlag.PlantedAtRound;
            result.Message = $"flag was planted {currentRound.Number - adFlag.PlantedAtRound} ticks ago (lifetime {lifetimeTicks})";
            return result;
        }

        var alreadySubmitted = await db.AdAttacks.AnyAsync(
            a => a.AttackerParticipationId == attackerPart.Id && a.AdFlagId == adFlag.Id, token);
        if (alreadySubmitted)
        {
            result.Status = "duplicate";
            result.FlagPlantedAtRound = adFlag.PlantedAtRound;
            result.Message = "you've already submitted this flag";
            return result;
        }

        var priorCapturers = await db.AdAttacks.CountAsync(a => a.AdFlagId == adFlag.Id, token);
        var points = AdScoring.AttackPoints(priorCapturers);

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
            result.Status = "duplicate";
            result.FlagPlantedAtRound = adFlag.PlantedAtRound;
            result.Message = "already submitted";
            return result;
        }

        logger.SystemLog(
            $"A&D attack landed: attacker={attackerPart.Id} victim={victimPart.Id} chal={adFlag.AdTeamService.ChallengeId} round={currentRound.Number} pts={points:F2}",
            TaskStatus.Success, LogLevel.Information);

        result.Status = "accepted";
        result.Points = points;
        result.FlagPlantedAtRound = adFlag.PlantedAtRound;
        return result;
    }

    private async Task<Participation?> ResolveTeamApiTokenAsync(int gameId, CancellationToken token)
    {
        var auth = Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!auth.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        var presented = auth[scheme.Length..].Trim();
        if (!presented.StartsWith(AdTokenUtils.TokenPrefix, StringComparison.Ordinal))
            return null;

        var hash = AdTokenUtils.Hash(presented, configService.GetXorKey());

        var row = await db.AdTeamApiTokens
            .Include(t => t.Participation).ThenInclude(p => p.Members)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, token);

        if (row is null
            || row.Participation.GameId != gameId
            || row.Participation.Status != ParticipationStatus.Accepted)
            return null;

        // Verify the user this token belongs to is *still* on the team. This
        // is what makes member-kick an instant revocation: even if the kicked
        // member kept their token, their UserParticipation row was removed by
        // the kick flow and this lookup fails.
        var stillAMember = row.Participation.Members.Any(m => m.UserId == row.UserId);
        if (!stillAMember) return null;

        row.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        return row.Participation;
    }

    private async Task<Participation?> ResolveUserParticipationAsync(int gameId, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return null;

        return await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == gameId
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
    }

    /// <summary>
    /// Generate or rotate the caller's own A&amp;D API token for this game.
    /// Any team member can manage their own token — no captain check.
    /// Returns the plaintext token exactly once.
    /// </summary>
    [RequireUser]
    [HttpPost("Token")]
    [ProducesResponseType(typeof(AdTokenGenerateResultModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> RotateToken(int id, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);

        if (participation is null) return Forbid();

        var plaintext = AdTokenUtils.GeneratePlaintext();
        var hint = AdTokenUtils.BuildHint(plaintext);
        var hash = AdTokenUtils.Hash(plaintext, configService.GetXorKey());
        var now = DateTimeOffset.UtcNow;

        var existing = await db.AdTeamApiTokens
            .FirstOrDefaultAsync(t => t.UserId == user.Id && t.ParticipationId == participation.Id, token);

        if (existing is null)
        {
            existing = new AdTeamApiToken
            {
                UserId = user.Id,
                ParticipationId = participation.Id,
                TokenHash = hash,
                Hint = hint,
                CreatedAt = now,
                LastRotatedAt = now
            };
            await db.AdTeamApiTokens.AddAsync(existing, token);
        }
        else
        {
            existing.TokenHash = hash;
            existing.Hint = hint;
            existing.LastRotatedAt = now;
            existing.LastUsedAt = null;
        }

        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D user token rotated: user={user.Id} participation={participation.Id}",
            TaskStatus.Success, LogLevel.Information);

        return Ok(new AdTokenGenerateResultModel
        {
            Token = plaintext,
            Hint = hint,
            RotatedAt = now
        });
    }

    /// <summary>
    /// Read the hint for the caller's own A&amp;D API token. Per-user; no
    /// captain check needed (each user manages their own token). Never
    /// reveals the plaintext.
    /// </summary>
    [RequireUser]
    [HttpGet("Token")]
    [ProducesResponseType(typeof(AdTokenHintModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTokenHint(int id, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);

        if (participation is null) return Forbid();

        var existing = await db.AdTeamApiTokens
            .FirstOrDefaultAsync(t => t.UserId == user.Id && t.ParticipationId == participation.Id, token);

        if (existing is null)
            return Ok(new AdTokenHintModel { Exists = false, CanManage = true });

        return Ok(new AdTokenHintModel
        {
            Exists = true,
            Hint = existing.Hint,
            CreatedAt = existing.CreatedAt,
            LastRotatedAt = existing.LastRotatedAt,
            LastUsedAt = existing.LastUsedAt,
            CanManage = true
        });
    }

    /// <summary>
    /// Revoke the caller's own A&amp;D API token. Per-user — does not affect
    /// other team members' tokens. After this call, the caller's Bearer-token
    /// submissions stop working until they generate a new one.
    /// </summary>
    [RequireUser]
    [HttpDelete("Token")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeToken(int id, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);

        if (participation is null) return Forbid();

        var existing = await db.AdTeamApiTokens
            .FirstOrDefaultAsync(t => t.UserId == user.Id && t.ParticipationId == participation.Id, token);

        if (existing is null)
            return NoContent();

        db.AdTeamApiTokens.Remove(existing);
        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D user token revoked: user={user.Id} participation={participation.Id}",
            TaskStatus.Success, LogLevel.Information);

        return NoContent();
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

        // Cooldown check — game-wide policy.
        var cooldownMinutes = await db.Games
            .Where(g => g.Id == id)
            .Select(g => g.AdResetCooldownMinutes)
            .FirstOrDefaultAsync(token) ?? 5;
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

        // Reset cooldown is game-wide policy — fetch once, apply to every service.
        var cooldownMinutes = await db.Games
            .Where(g => g.Id == id)
            .Select(g => g.AdResetCooldownMinutes)
            .FirstOrDefaultAsync(token) ?? 5;

        var serviceModels = services.Select(s =>
        {
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
    /// List every other team's container IP per enabled A&amp;D challenge,
    /// plus the last health-check verdict for each. Attack-first players need
    /// this to know where to aim — without it A&amp;D is unplayable. Caller's
    /// own team rows are excluded so the response is directly usable as a
    /// targets list. Empty teams[] until the warmup round has elapsed.
    /// </summary>
    /// <remarks>
    /// Dual auth (same shape as Submit): cookie session OR
    /// <c>Authorization: Bearer ad_...</c> token. Wired up so exploit
    /// scripts can poll without a browser.
    /// </remarks>
    [HttpGet("Targets")]
    [ProducesResponseType(typeof(AdTargetsModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Targets(int id, CancellationToken token)
    {
        var caller = await ResolveTeamApiTokenAsync(id, token)
                     ?? await ResolveUserParticipationAsync(id, token);
        if (caller is null) return Unauthorized();

        var currentRound = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderByDescending(r => r.Number)
            .Select(r => r.Number)
            .FirstOrDefaultAsync(token);

        var enabledChallenges = await db.GameChallenges
            .Where(c => c.GameId == id && c.Type == ChallengeType.AttackDefense && c.IsEnabled)
            .OrderBy(c => c.Id)
            .ToListAsync(token);

        var result = new AdTargetsModel { CurrentRound = currentRound };
        if (currentRound == 0)
            return Ok(result); // warmup — no targets yet

        var services = await db.AdTeamServices
            .Where(ts => ts.Participation.GameId == id
                && ts.ParticipationId != caller.Id
                && ts.Container != null
                && ts.Participation.Status == ParticipationStatus.Accepted)
            .Include(ts => ts.Container)
            .Include(ts => ts.Participation).ThenInclude(p => p.Team)
            .Include(ts => ts.Participation).ThenInclude(p => p.Division)
            .ToListAsync(token);

        var serviceIds = services.Select(s => s.Id).ToList();
        var lastChecks = serviceIds.Count == 0 ? [] :
            await db.AdCheckResults
                .Where(c => serviceIds.Contains(c.AdTeamServiceId))
                .GroupBy(c => c.AdTeamServiceId)
                .Select(g => g.OrderByDescending(c => c.CheckedAt).First())
                .ToListAsync(token);
        var lastChecksByService = lastChecks.ToDictionary(c => c.AdTeamServiceId);

        // Tick is game-wide — one value for every challenge row below.
        var tickSeconds = await db.Games
            .Where(g => g.Id == id)
            .Select(g => g.AdTickSeconds)
            .FirstOrDefaultAsync(token) ?? 120;

        foreach (var chal in enabledChallenges)
        {
            var row = new AdChallengeTargets
            {
                ChallengeId = chal.Id,
                Title = chal.Title,
                TickSeconds = tickSeconds,
                Teams = services
                    .Where(s => s.ChallengeId == chal.Id)
                    .OrderBy(s => s.Participation.Team.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new AdTeamTarget
                    {
                        ParticipationId = s.ParticipationId,
                        TeamName = s.Participation.Team.Name,
                        Division = s.Participation.Division?.Name,
                        Ip = s.Container?.IP,
                        Port = s.Container?.Port,
                        LastCheckStatus = lastChecksByService.GetValueOrDefault(s.Id)?.Status.ToString(),
                    })
                    .ToList()
            };
            result.Challenges.Add(row);
        }

        return Ok(result);
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

        // The per-service columns: enabled A&D challenges in a stable order.
        var challenges = await db.GameChallenges
            .Where(c => c.GameId == id && c.Type == ChallengeType.AttackDefense && c.IsEnabled)
            .OrderBy(c => c.Id)
            .Select(c => new AdScoreboardChallenge { ChallengeId = c.Id, Title = c.Title })
            .ToListAsync(token);
        var challengeIds = challenges.Select(c => c.ChallengeId).ToList();

        // Scoring is PER (team, service) — the FAUST/EnoEngine model. The team
        // total is the sum of service nets. Defense in particular MUST be
        // per-service: Σ caps_s^0.75 ≠ (Σ caps_s)^0.75, and being broken on
        // two services should hurt more than the combined sub-linear curve.

        // Attack points + flags captured, per (attacker, challenge).
        var attackByCell = await db.AdAttacks
            .Where(a => partIds.Contains(a.AttackerParticipationId) && challengeIds.Contains(a.ChallengeId))
            .GroupBy(a => new { a.AttackerParticipationId, a.ChallengeId })
            .Select(g => new { g.Key.AttackerParticipationId, g.Key.ChallengeId, Points = g.Sum(a => a.Points), Count = g.Count() })
            .ToListAsync(token);
        var attackLookup = attackByCell.ToDictionary(x => (x.AttackerParticipationId, x.ChallengeId), x => (x.Points, x.Count));

        // Times captured, per (victim, challenge).
        var defenseByCell = await db.AdAttacks
            .Where(a => partIds.Contains(a.VictimParticipationId) && challengeIds.Contains(a.ChallengeId))
            .GroupBy(a => new { a.VictimParticipationId, a.ChallengeId })
            .Select(g => new { g.Key.VictimParticipationId, g.Key.ChallengeId, Count = g.Count() })
            .ToListAsync(token);
        var defenseLookup = defenseByCell.ToDictionary(x => (x.VictimParticipationId, x.ChallengeId), x => x.Count);

        // SLA credit SUM per (team, challenge) — precomputed per tick (see AdScoring).
        var slaByCell = await db.AdCheckResults
            .Join(db.AdTeamServices,
                c => c.AdTeamServiceId, ts => ts.Id,
                (c, ts) => new { ts.ParticipationId, ts.ChallengeId, c.SlaCredit })
            .Where(x => partIds.Contains(x.ParticipationId) && challengeIds.Contains(x.ChallengeId))
            .GroupBy(x => new { x.ParticipationId, x.ChallengeId })
            .Select(g => new { g.Key.ParticipationId, g.Key.ChallengeId, Credit = g.Sum(x => x.SlaCredit) })
            .ToListAsync(token);
        var slaLookup = slaByCell.ToDictionary(x => (x.ParticipationId, x.ChallengeId), x => x.Credit);

        // Latest check status per (team, challenge), for the status dot on each cell.
        var statusRows = await db.AdTeamServices
            .Where(ts => partIds.Contains(ts.ParticipationId) && challengeIds.Contains(ts.ChallengeId))
            .Select(ts => new
            {
                ts.ParticipationId,
                ts.ChallengeId,
                Last = db.AdCheckResults
                    .Where(c => c.AdTeamServiceId == ts.Id)
                    .OrderByDescending(c => c.CheckedAt)
                    .Select(c => (AdCheckStatus?)c.Status)
                    .FirstOrDefault()
            })
            .ToListAsync(token);
        var statusLookup = statusRows.ToDictionary(x => (x.ParticipationId, x.ChallengeId), x => x.Last);

        var activeTeams = teams.Count;

        var rows = teams.Select(p =>
        {
            var services = new List<AdServiceScore>(challenges.Count);
            double tAttack = 0, tDefense = 0, tSla = 0;
            int tFlags = 0, tCaptured = 0;

            foreach (var ch in challenges)
            {
                var key = (p.Id, ch.ChallengeId);
                var (atkPts, atkCnt) = attackLookup.GetValueOrDefault(key, (0d, 0));
                var caps = defenseLookup.GetValueOrDefault(key, 0);
                var defLoss = AdScoring.DefenseLoss(caps);
                var sla = AdScoring.SlaPoints(slaLookup.GetValueOrDefault(key, 0), activeTeams);
                var net = atkPts + sla - defLoss;

                tAttack += atkPts; tDefense += defLoss; tSla += sla;
                tFlags += atkCnt; tCaptured += caps;

                services.Add(new AdServiceScore
                {
                    ChallengeId = ch.ChallengeId,
                    AttackPoints = atkPts,
                    DefenseLoss = defLoss,
                    SlaPoints = sla,
                    Net = net,
                    FlagsCaptured = atkCnt,
                    TimesCaptured = caps,
                    LastCheckStatus = statusLookup.GetValueOrDefault(key)?.ToString()
                });
            }

            return new AdTeamScoreRow
            {
                ParticipationId = p.Id,
                TeamId = p.TeamId,
                TeamName = p.Team.Name,
                Division = p.Division?.Name,
                AttackPoints = tAttack,
                DefenseLoss = tDefense,
                SlaPoints = tSla,
                Total = tAttack + tSla - tDefense,
                TimesCaptured = tCaptured,
                FlagsCaptured = tFlags,
                Services = services
            };
        })
        .OrderByDescending(r => r.Total)
        .ToList();

        for (int i = 0; i < rows.Count; i++)
            rows[i].Rank = i + 1;

        return Ok(new AdScoreboardModel
        {
            LatestRound = latestRound,
            Challenges = challenges,
            Teams = rows
        });
    }

    /// <summary>
    /// Per-round, per-team cumulative score timeline for the A&amp;D scoreboard
    /// chart. Mirrors what GameRepository builds for the jeopardy ScoreTimeLine
    /// component. Public; respects the game's hidden flag.
    /// </summary>
    [HttpGet("Timeline")]
    [ProducesResponseType(typeof(AdScoreTimelineModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Timeline(int id, CancellationToken token)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == id, token);
        if (game is null || game.Hidden) return NotFound();

        var hasAd = await db.GameChallenges.AnyAsync(
            c => c.GameId == id && c.Type == ChallengeType.AttackDefense, token);
        if (!hasAd) return NotFound();

        var rounds = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderBy(r => r.Number)
            .ToListAsync(token);

        var teams = await db.Participations
            .Where(p => p.GameId == id && p.Status == ParticipationStatus.Accepted)
            .Include(p => p.Team)
            .Include(p => p.Division)
            .ToListAsync(token);

        var result = new AdScoreTimelineModel
        {
            LatestRound = rounds.LastOrDefault()?.Number ?? 0,
            StartedAt = rounds.FirstOrDefault()?.StartedAt,
            EndsAt = rounds.LastOrDefault()?.EndsAt,
        };

        if (rounds.Count == 0 || teams.Count == 0)
            return Ok(result);

        var partIds = teams.Select(p => p.Id).ToHashSet();

        // Pre-aggregate attacks once. Attack is linear → by (attacker, round).
        // Defense is per-service (Σ caps_s^0.75) → by (victim, challenge, round)
        // so the timeline's defense matches the scoreboard's per-service total.
        var attacks = await db.AdAttacks
            .Where(a => partIds.Contains(a.AttackerParticipationId)
                     || partIds.Contains(a.VictimParticipationId))
            .Select(a => new
            {
                a.AttackerParticipationId,
                a.VictimParticipationId,
                a.ChallengeId,
                a.SubmittedAtRound,
                a.Points
            })
            .ToListAsync(token);

        var attackByTeamRound = attacks
            .Where(a => partIds.Contains(a.AttackerParticipationId))
            .GroupBy(a => (a.AttackerParticipationId, a.SubmittedAtRound))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Points));

        // (victim, round) → list of (challengeId, captureCount) for that round.
        var capturesByVictimRound = attacks
            .Where(a => partIds.Contains(a.VictimParticipationId))
            .GroupBy(a => (a.VictimParticipationId, a.SubmittedAtRound, a.ChallengeId))
            .GroupBy(g => (g.Key.VictimParticipationId, g.Key.SubmittedAtRound))
            .ToDictionary(
                outer => outer.Key,
                outer => outer.Select(g => (g.Key.ChallengeId, Count: g.Count())).ToList());

        // Map each AdCheckResult to (participationId, credit, time) once.
        var checks = await db.AdCheckResults
            .Join(db.AdTeamServices,
                c => c.AdTeamServiceId, ts => ts.Id,
                (c, ts) => new { ts.ParticipationId, c.SlaCredit, c.CheckedAt })
            .Where(x => partIds.Contains(x.ParticipationId))
            .ToListAsync(token);

        // Index checks per team for fast per-round window queries.
        var checksByTeam = checks
            .GroupBy(c => c.ParticipationId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.CheckedAt).ToList()
            );

        var activeTeams = teams.Count;

        foreach (var team in teams)
        {
            var timeline = new AdTeamTimeline
            {
                ParticipationId = team.Id,
                TeamId = team.TeamId,
                TeamName = team.Team.Name,
                Division = team.Division?.Name,
            };

            double cumAttack = 0;
            double cumSlaCredit = 0;
            // Cumulative captures PER service — defense is Σ caps_s^0.75, so we
            // can't collapse to a single running total (that would understate
            // the penalty vs the per-service scoreboard).
            var cumCapsByChallenge = new Dictionary<int, int>();

            // Walk team's checks in order; advance pointer per round.
            var teamChecks = checksByTeam.GetValueOrDefault(team.Id) ?? [];
            var checkIdx = 0;

            foreach (var round in rounds)
            {
                cumAttack += attackByTeamRound.GetValueOrDefault((team.Id, round.Number), 0);

                foreach (var (challengeId, count) in
                         capturesByVictimRound.GetValueOrDefault((team.Id, round.Number), []))
                {
                    cumCapsByChallenge[challengeId] = cumCapsByChallenge.GetValueOrDefault(challengeId) + count;
                }

                while (checkIdx < teamChecks.Count && teamChecks[checkIdx].CheckedAt < round.EndsAt)
                {
                    cumSlaCredit += teamChecks[checkIdx].SlaCredit;
                    checkIdx++;
                }

                var defenseLoss = cumCapsByChallenge.Values.Sum(AdScoring.DefenseLoss);
                var slaPoints = AdScoring.SlaPoints(cumSlaCredit, activeTeams);
                var total = cumAttack - defenseLoss + slaPoints;

                timeline.Items.Add(new AdTimelinePoint
                {
                    Round = round.Number,
                    Time = round.EndsAt,
                    Score = total
                });
            }

            result.Teams.Add(timeline);
        }

        return Ok(result);
    }

    /// <summary>
    /// Download a per-user WireGuard config (.conf) for accessing the A&amp;D
    /// network. Generates a fresh X25519 keypair + assigns a /32 from the
    /// configured client CIDR on first call; subsequent calls return the same
    /// peer's .conf so the matching server-side entry rendered by
    /// <see cref="AdWireGuardSyncService"/> stays valid.
    ///
    /// <para>Server pubkey comes from the shared <c>/wg-config/server.pub</c>
    /// file written by the wireguard sidecar on first boot. Server endpoint
    /// and other knobs read from <c>IConfiguration</c> (env vars
    /// <c>Ad__Vpn__ServerEndpoint</c>, <c>Ad__Vpn__ClientCidr</c>,
    /// <c>Ad__Vpn__Dns</c>, <c>Ad__Vpn__AllowedIps</c>) and fall back to
    /// in-host-test defaults when unset.</para>
    /// </summary>
    [RequireUser]
    [HttpGet("Vpn/Config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadVpnConfig(
        int id,
        [FromServices] IConfiguration config,
        [FromServices] AdVpnTopology topology,
        CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (participation is null) return Forbid();

        var hasAd = await db.GameChallenges.AnyAsync(
            c => c.GameId == id && c.Type == ChallengeType.AttackDefense, token);
        if (!hasAd) return NotFound(new RequestResponse("This game has no A&D challenges"));

        var configDir = config["Ad:Vpn:ConfigDir"] ?? "/wg-config";
        var serverPubKeyPath = Path.Combine(configDir, "server.pub");

        // Server pubkey comes from the sidecar's shared volume. Env var
        // override exists for tests / out-of-cluster deployments.
        var serverPublicKey =
            config["Ad:Vpn:ServerPublicKey"]
            ?? (System.IO.File.Exists(serverPubKeyPath)
                ? (await System.IO.File.ReadAllTextAsync(serverPubKeyPath, token)).Trim()
                : null);

        if (string.IsNullOrEmpty(serverPublicKey))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new RequestResponse(
                "WireGuard server keypair not yet provisioned by the sidecar. Make sure the wireguard service is running and the shared volume is mounted at " + configDir));

        var serverEndpoint = config["Ad:Vpn:ServerEndpoint"] ?? "127.0.0.1:51820";
        var clientCidr = config["Ad:Vpn:ClientCidr"] ?? "10.13.37.0/24";
        var dns = config["Ad:Vpn:Dns"] ?? "1.1.1.1";

        // AllowedIps order of precedence:
        //   1. explicit Ad__Vpn__AllowedIps env override (operator knows best)
        //   2. live-discovered challenge subnets via AdVpnTopology (the path
        //      that "just works" without manual compose / env config)
        //   3. fallback to just the VPN subnet (degraded mode — clients can
        //      only reach each other, not challenges; the operator should
        //      notice + investigate)
        string allowedIps;
        if (config["Ad:Vpn:AllowedIps"] is { Length: > 0 } configured)
        {
            allowedIps = configured;
        }
        else
        {
            var discovered = await topology.GetChallengeSubnetsAsync(configDir, token);
            allowedIps = discovered.Count > 0
                ? $"{clientCidr}, {string.Join(", ", discovered)}"
                : clientCidr;
        }

        var peer = await db.AdVpnPeers
            .FirstOrDefaultAsync(p => p.UserId == user.Id && p.ParticipationId == participation.Id, token);

        string privKeyBase64;
        var xorKey = configService.GetXorKey();

        if (peer is null)
        {
            var (pub, priv) = AdVpnKeys.GenerateX25519KeyPair();
            var usedIps = await db.AdVpnPeers
                .Where(p => p.Participation.GameId == id)
                .Select(p => p.AssignedIp)
                .ToListAsync(token);

            string assignedIp;
            try { assignedIp = AdVpnKeys.AssignNextIp(clientCidr, usedIps); }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new RequestResponse(ex.Message));
            }

            peer = new AdVpnPeer
            {
                UserId = user.Id,
                ParticipationId = participation.Id,
                PublicKey = Convert.ToBase64String(pub),
                PrivateKey = AdVpnKeys.WrapPrivateKey(priv, xorKey),
                AssignedIp = assignedIp,
            };
            await db.AdVpnPeers.AddAsync(peer, token);
            await db.SaveChangesAsync(token);

            privKeyBase64 = Convert.ToBase64String(priv);

            logger.SystemLog(
                $"A&D VPN peer provisioned: user={user.Id} participation={participation.Id} ip={assignedIp}",
                TaskStatus.Success, LogLevel.Information);
        }
        else
        {
            privKeyBase64 = AdVpnKeys.UnwrapPrivateKey(peer.PrivateKey, xorKey);
        }

        var safeUserName = string.Concat((user.UserName ?? "player")
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
        if (string.IsNullOrEmpty(safeUserName)) safeUserName = "player";

        var conf =
$"""
# WireGuard config for {user.UserName} — A&D game {id}
# Generated {DateTimeOffset.UtcNow:u}
# Pubkey: {peer.PublicKey}
# Assigned IP: {peer.AssignedIp}

[Interface]
PrivateKey = {privKeyBase64}
Address = {peer.AssignedIp}/32
DNS = {dns}

[Peer]
PublicKey = {serverPublicKey}
Endpoint = {serverEndpoint}
AllowedIPs = {allowedIps}
PersistentKeepalive = 25
""";

        var bytes = System.Text.Encoding.UTF8.GetBytes(conf);
        return File(bytes, "text/plain", $"ad-game-{id}-{safeUserName}.conf");
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

    /// <summary>
    /// Upload an OpenSSH public key — the recommended path. The private
    /// half never crosses the wire. Overwrites the caller's existing key
    /// if present (rotation = re-upload). One slot per (User, Game) — the
    /// (UserId, ParticipationId) unique index enforces this at the DB.
    /// </summary>
    [RequireUser]
    [HttpPost("Ssh/Key")]
    [ProducesResponseType(typeof(AdSshKeyInfoModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadSshKey(
        int id,
        [FromBody] AdSshKeyUploadModel model,
        [FromServices] IConfiguration config,
        CancellationToken token)
    {
        if (!ModelState.IsValid)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Model_ValidationFailed)]));

        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (participation is null) return Forbid();

        AdSshKeyUtils.ParsedKey parsed;
        try { parsed = AdSshKeyUtils.Parse(model.PublicKey); }
        catch (FormatException e) { return BadRequest(new RequestResponse(e.Message)); }

        var now = DateTimeOffset.UtcNow;
        var existing = await db.AdTeamSshKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ParticipationId == participation.Id, token);

        if (existing is null)
        {
            existing = new AdTeamSshKey
            {
                UserId = user.Id,
                ParticipationId = participation.Id,
                PublicKey = model.PublicKey.Trim(),
                PrivateKey = null,
                Algorithm = parsed.Algorithm,
                Fingerprint = parsed.Fingerprint,
                PlatformGenerated = false,
                CreatedAt = now
            };
            await db.AdTeamSshKeys.AddAsync(existing, token);
        }
        else
        {
            existing.PublicKey = model.PublicKey.Trim();
            existing.PrivateKey = null;
            existing.Algorithm = parsed.Algorithm;
            existing.Fingerprint = parsed.Fingerprint;
            existing.PlatformGenerated = false;
            existing.RevokedAt = null;
            existing.CreatedAt = now;
            existing.LastUsedAt = null;
        }

        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D SSH key uploaded: user={user.Id} game={id} fp={parsed.Fingerprint}",
            TaskStatus.Success, LogLevel.Information);

        return Ok(BuildSshInfo(existing, config));
    }

    /// <summary>
    /// Server-generate an ed25519 keypair. The private key is returned
    /// once in this response; the ciphertext is also stored at-rest so
    /// the platform can re-emit it if the user loses the file before
    /// the game ends (operator decision — disable later if undesired).
    /// </summary>
    [RequireUser]
    [HttpPost("Ssh/Key/Generate")]
    [ProducesResponseType(typeof(AdSshKeyGeneratedModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> GenerateSshKey(
        int id,
        [FromServices] IConfiguration config,
        CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (participation is null) return Forbid();

        var comment = $"gzctf-ad-{user.UserName}-game{id}";
        var kp = AdSshKeyUtils.GenerateEd25519(comment);
        var xorKey = configService.GetXorKey();
        var wrapped = AdSshKeyUtils.WrapPrivateKey(kp.PrivateKeyOpenSsh, xorKey);
        var now = DateTimeOffset.UtcNow;

        var existing = await db.AdTeamSshKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ParticipationId == participation.Id, token);

        if (existing is null)
        {
            existing = new AdTeamSshKey
            {
                UserId = user.Id,
                ParticipationId = participation.Id,
                PublicKey = kp.PublicKeyOpenSsh,
                PrivateKey = wrapped,
                Algorithm = "ssh-ed25519",
                Fingerprint = kp.Fingerprint,
                PlatformGenerated = true,
                CreatedAt = now
            };
            await db.AdTeamSshKeys.AddAsync(existing, token);
        }
        else
        {
            existing.PublicKey = kp.PublicKeyOpenSsh;
            existing.PrivateKey = wrapped;
            existing.Algorithm = "ssh-ed25519";
            existing.Fingerprint = kp.Fingerprint;
            existing.PlatformGenerated = true;
            existing.RevokedAt = null;
            existing.CreatedAt = now;
            existing.LastUsedAt = null;
        }

        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D SSH keypair generated: user={user.Id} game={id} fp={kp.Fingerprint}",
            TaskStatus.Success, LogLevel.Information);

        return Ok(new AdSshKeyGeneratedModel
        {
            Algorithm = "ssh-ed25519",
            PublicKey = kp.PublicKeyOpenSsh,
            PrivateKey = kp.PrivateKeyOpenSsh,
            Fingerprint = kp.Fingerprint,
            CreatedAt = now
        });
    }

    /// <summary>
    /// Metadata for the caller's installed SSH key (no plaintext returned).
    /// <see cref="AdSshKeyInfoModel.Exists"/> false on first call so the
    /// UI can render the upload form.
    /// </summary>
    [RequireUser]
    [HttpGet("Ssh/Key")]
    [ProducesResponseType(typeof(AdSshKeyInfoModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSshKey(
        int id,
        [FromServices] IConfiguration config,
        CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (participation is null) return Forbid();

        var existing = await db.AdTeamSshKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ParticipationId == participation.Id, token);

        if (existing is null || existing.RevokedAt is not null)
            return Ok(new AdSshKeyInfoModel { Exists = false, JumpHost = ResolveJumpHost(config) });

        return Ok(BuildSshInfo(existing, config));
    }

    /// <summary>
    /// Revoke the caller's SSH key. Next jump-host connection from the
    /// matching pubkey is refused at <c>AuthorizedKeysCommand</c> time.
    /// </summary>
    [RequireUser]
    [HttpDelete("Ssh/Key")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeSshKey(int id, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (participation is null) return Forbid();

        var existing = await db.AdTeamSshKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ParticipationId == participation.Id, token);
        if (existing is null) return NoContent();

        db.AdTeamSshKeys.Remove(existing);
        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D SSH key revoked: user={user.Id} game={id}",
            TaskStatus.Success, LogLevel.Information);
        return NoContent();
    }

    private static AdSshKeyInfoModel BuildSshInfo(AdTeamSshKey key, IConfiguration config) => new()
    {
        Exists = true,
        Algorithm = key.Algorithm,
        Fingerprint = key.Fingerprint,
        PlatformGenerated = key.PlatformGenerated,
        CreatedAt = key.CreatedAt,
        LastUsedAt = key.LastUsedAt,
        JumpHost = ResolveJumpHost(config)
    };

    private static string ResolveJumpHost(IConfiguration config)
    {
        var host = config["Ad:Ssh:PublicHost"];
        if (string.IsNullOrWhiteSpace(host))
            host = config["PublicEntry"] ?? "localhost";
        var port = config["Ad:Ssh:PublicPort"];
        return string.IsNullOrWhiteSpace(port) ? $"{host}:2222" : $"{host}:{port}";
    }
}
