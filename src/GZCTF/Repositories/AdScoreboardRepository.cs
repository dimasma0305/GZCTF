using GZCTF.Models.Request.Game;
using GZCTF.Models.Response.Admin;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Repositories;

/// <summary>
/// Caches the A&amp;D scoreboard + timeline so they aren't recomputed per request
/// (the aggregations scan tables that grow every tick). Build logic moved here
/// verbatim from <c>AdGameController</c>, with two scaling fixes:
///   - the per-service "latest status" reads by the indexed <c>AdRoundId</c>
///     instead of an unindexed <c>CheckedAt</c> sort;
///   - the timeline aggregates SLA per (team, round) in SQL and emits at most
///     <see cref="MaxTimelinePoints"/> points, so it's O(teams) not O(teams × rounds).
/// </summary>
public class AdScoreboardRepository(
    ILogger<AdScoreboardRepository> logger,
    CacheHelper cacheHelper,
    AppDbContext context) : RepositoryBase(context), IAdScoreboardRepository
{
    /// <summary>Cap on timeline points per team — the chart doesn't need one point
    /// per round (a long game is thousands), so we downsample to this many.</summary>
    private const int MaxTimelinePoints = 150;

    public Task<AdScoreboardModel> GetScoreboardAsync(int gameId, DateTimeOffset? cutoff, CancellationToken token = default)
        => cacheHelper.GetOrCreateAsync(logger,
            cutoff == null ? CacheKey.AdScoreBoard(gameId) : CacheKey.AdScoreBoardFrozen(gameId),
            entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromDays(7);
                return GenScoreboardAsync(gameId, cutoff, token);
            }, token: token);

    public Task<AdScoreboardModel?> TryGetScoreboardAsync(int gameId, bool frozen, CancellationToken token = default)
        => cacheHelper.GetAsync<AdScoreboardModel>(
            frozen ? CacheKey.AdScoreBoardFrozen(gameId) : CacheKey.AdScoreBoard(gameId), token);

    public Task<AdScoreTimelineModel> GetTimelineAsync(int gameId, DateTimeOffset? cutoff, CancellationToken token = default)
        => cacheHelper.GetOrCreateAsync(logger,
            cutoff == null ? CacheKey.AdTimeline(gameId) : CacheKey.AdTimelineFrozen(gameId),
            entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromDays(7);
                return GenTimelineAsync(gameId, cutoff, token);
            }, token: token);

    public Task<AdScoreTimelineModel?> TryGetTimelineAsync(int gameId, bool frozen, CancellationToken token = default)
        => cacheHelper.GetAsync<AdScoreTimelineModel>(
            frozen ? CacheKey.AdTimelineFrozen(gameId) : CacheKey.AdTimeline(gameId), token);

    public async Task<AdScoreboardModel> GenScoreboardAsync(int gameId, DateTimeOffset? cutoff, CancellationToken token = default)
    {
        var freeze = await Context.Games
            .Where(g => g.Id == gameId).Select(g => g.FreezeTimeUtc).FirstOrDefaultAsync(token);

        var latestRound = await Context.AdRounds
            .Where(r => r.GameId == gameId && (cutoff == null || r.StartedAt <= cutoff))
            .OrderByDescending(r => r.Number)
            .Select(r => r.Number)
            .FirstOrDefaultAsync(token);

        var teams = await Context.Participations
            .Where(p => p.GameId == gameId && p.Status == ParticipationStatus.Accepted)
            .Include(p => p.Team)
            .Include(p => p.Division)
            .ToListAsync(token);

        var partIds = teams.Select(p => p.Id).ToList();

        var challengeRows = await Context.GameChallenges
            .Where(c => c.GameId == gameId && c.IsEnabled
                && (c.Type == ChallengeType.AttackDefense || c.Type == ChallengeType.KingOfTheHill))
            .OrderBy(c => c.Category).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Title, c.Category, c.Type })
            .ToListAsync(token);

        var kothChallengeIds = challengeRows
            .Where(c => c.Type == ChallengeType.KingOfTheHill).Select(c => c.Id).ToHashSet();

        var challenges = challengeRows.Select(c => new AdScoreboardChallenge
        {
            ChallengeId = c.Id,
            Title = c.Title,
            Category = c.Category.ToString()
        }).ToList();
        // A&D-only ids for the attack/defense/SLA aggregations below.
        var challengeIds = challengeRows
            .Where(c => c.Type == ChallengeType.AttackDefense).Select(c => c.Id).ToList();

        // KotH hold score per (controlling team, hill) = Σ HoldCredit − Penalty.
        Dictionary<(int, int), double> kothLookup = new();
        Dictionary<int, AdCheckStatus?> kothStatusByChallenge = new();
        if (kothChallengeIds.Count > 0)
        {
            var kothByCell = await Context.KothControlResults
                .Where(r => r.GameId == gameId && r.ControllingParticipationId != null
                    && kothChallengeIds.Contains(r.ChallengeId)
                    && (cutoff == null || r.CheckedAt <= cutoff))
                .GroupBy(r => new { Pid = r.ControllingParticipationId!.Value, r.ChallengeId })
                .Select(g => new { g.Key.Pid, g.Key.ChallengeId, Score = g.Sum(x => x.HoldCredit - x.Penalty) })
                .ToListAsync(token);
            kothLookup = kothByCell.ToDictionary(x => (x.Pid, x.ChallengeId), x => x.Score);

            // Latest functional verdict per hill (shared target → per-challenge, not per-team).
            foreach (var cid in kothChallengeIds)
                kothStatusByChallenge[cid] = await Context.KothControlResults
                    .Where(r => r.ChallengeId == cid && (cutoff == null || r.CheckedAt <= cutoff))
                    .OrderByDescending(r => r.AdRoundId)
                    .Select(r => (AdCheckStatus?)r.Status)
                    .FirstOrDefaultAsync(token);
        }

        // Attack points + flags captured, per (attacker, challenge).
        var attackByCell = await Context.AdAttacks
            .Where(a => partIds.Contains(a.AttackerParticipationId) && challengeIds.Contains(a.ChallengeId)
                     && (cutoff == null || a.SubmittedAt <= cutoff))
            .GroupBy(a => new { a.AttackerParticipationId, a.ChallengeId })
            .Select(g => new { g.Key.AttackerParticipationId, g.Key.ChallengeId, Points = g.Sum(a => a.Points), Count = g.Count() })
            .ToListAsync(token);
        var attackLookup = attackByCell.ToDictionary(x => (x.AttackerParticipationId, x.ChallengeId), x => (x.Points, x.Count));

        // Times captured, per (victim, challenge).
        var defenseByCell = await Context.AdAttacks
            .Where(a => partIds.Contains(a.VictimParticipationId) && challengeIds.Contains(a.ChallengeId)
                     && (cutoff == null || a.SubmittedAt <= cutoff))
            .GroupBy(a => new { a.VictimParticipationId, a.ChallengeId })
            .Select(g => new { g.Key.VictimParticipationId, g.Key.ChallengeId, Count = g.Count() })
            .ToListAsync(token);
        var defenseLookup = defenseByCell.ToDictionary(x => (x.VictimParticipationId, x.ChallengeId), x => x.Count);

        // SLA credit SUM per (team, challenge). Live view reads the per-service
        // running total (O(teams), no scan of the unbounded check table); the
        // frozen view must sum the rows as-of the cutoff.
        Dictionary<(int, int), double> slaLookup;
        if (cutoff == null)
        {
            var slaByCell = await Context.AdTeamServices
                .Where(ts => partIds.Contains(ts.ParticipationId) && challengeIds.Contains(ts.ChallengeId))
                .GroupBy(ts => new { ts.ParticipationId, ts.ChallengeId })
                .Select(g => new { g.Key.ParticipationId, g.Key.ChallengeId, Credit = g.Sum(ts => ts.SlaCreditTotal) })
                .ToListAsync(token);
            slaLookup = slaByCell.ToDictionary(x => (x.ParticipationId, x.ChallengeId), x => x.Credit);
        }
        else
        {
            var slaByCell = await Context.AdCheckResults
                .Where(c => c.CheckedAt <= cutoff)
                .Join(Context.AdTeamServices,
                    c => c.AdTeamServiceId, ts => ts.Id,
                    (c, ts) => new { ts.ParticipationId, ts.ChallengeId, c.SlaCredit })
                .Where(x => partIds.Contains(x.ParticipationId) && challengeIds.Contains(x.ChallengeId))
                .GroupBy(x => new { x.ParticipationId, x.ChallengeId })
                .Select(g => new { g.Key.ParticipationId, g.Key.ChallengeId, Credit = g.Sum(x => x.SlaCredit) })
                .ToListAsync(token);
            slaLookup = slaByCell.ToDictionary(x => (x.ParticipationId, x.ChallengeId), x => x.Credit);
        }

        // Latest check status per (team, challenge). Ordered by AdRoundId (indexed
        // via the unique (AdTeamServiceId, AdRoundId) key) — same "latest" as
        // CheckedAt but index-backed instead of a per-service sort.
        var statusRows = await Context.AdTeamServices
            .Where(ts => partIds.Contains(ts.ParticipationId) && challengeIds.Contains(ts.ChallengeId))
            .Select(ts => new
            {
                ts.ParticipationId,
                ts.ChallengeId,
                Last = Context.AdCheckResults
                    .Where(c => c.AdTeamServiceId == ts.Id && (cutoff == null || c.CheckedAt <= cutoff))
                    .OrderByDescending(c => c.AdRoundId)
                    .Select(c => (AdCheckStatus?)c.Status)
                    .FirstOrDefault()
            })
            .ToListAsync(token);
        var statusLookup = statusRows.ToDictionary(x => (x.ParticipationId, x.ChallengeId), x => x.Last);

        var activeTeams = teams.Count;

        var rows = teams.Select(p =>
        {
            var services = new List<AdServiceScore>(challenges.Count);
            double tAttack = 0, tDefense = 0, tSla = 0, tKoth = 0;
            int tFlags = 0, tCaptured = 0;

            foreach (var ch in challenges)
            {
                var key = (p.Id, ch.ChallengeId);

                // King of the Hill column: hold points only (no per-team attack/
                // defense/SLA — the hill is shared). Status is the hill's verdict.
                if (kothChallengeIds.Contains(ch.ChallengeId))
                {
                    var kothPts = kothLookup.GetValueOrDefault(key, 0);
                    tKoth += kothPts;
                    services.Add(new AdServiceScore
                    {
                        ChallengeId = ch.ChallengeId,
                        IsKoth = true,
                        KothPoints = kothPts,
                        Net = kothPts,
                        LastCheckStatus = kothStatusByChallenge.GetValueOrDefault(ch.ChallengeId)?.ToString()
                    });
                    continue;
                }

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
                KothPoints = tKoth,
                Total = tAttack + tSla - tDefense + tKoth,
                TimesCaptured = tCaptured,
                FlagsCaptured = tFlags,
                Services = services
            };
        })
        .OrderByDescending(r => r.Total)
        .ToList();

        for (int i = 0; i < rows.Count; i++)
            rows[i].Rank = i + 1;

        return new AdScoreboardModel
        {
            LatestRound = latestRound,
            IsFrozenView = cutoff != null,
            Freeze = freeze,
            Challenges = challenges,
            Teams = rows
        };
    }

    public async Task<AdScoreTimelineModel> GenTimelineAsync(int gameId, DateTimeOffset? cutoff, CancellationToken token = default)
    {
        var rounds = await Context.AdRounds
            .Where(r => r.GameId == gameId && (cutoff == null || r.StartedAt <= cutoff))
            .OrderBy(r => r.Number)
            .Select(r => new { r.Number, r.StartedAt, r.EndsAt })
            .ToListAsync(token);

        var teams = await Context.Participations
            .Where(p => p.GameId == gameId && p.Status == ParticipationStatus.Accepted)
            .Include(p => p.Team)
            .Include(p => p.Division)
            .ToListAsync(token);

        var result = new AdScoreTimelineModel
        {
            LatestRound = rounds.Count > 0 ? rounds[^1].Number : 0,
            StartedAt = rounds.Count > 0 ? rounds[0].StartedAt : null,
            EndsAt = rounds.Count > 0 ? rounds[^1].EndsAt : null,
        };

        if (rounds.Count == 0 || teams.Count == 0)
            return result;

        var partIds = teams.Select(p => p.Id).ToHashSet();
        var activeTeams = teams.Count;

        // Aggregate in SQL per (team, round) instead of loading every row — avoids
        // pulling the whole (unbounded) AdCheckResults / AdAttacks tables into memory.
        var atkByTeamRound = (await Context.AdAttacks
                .Where(a => partIds.Contains(a.AttackerParticipationId) && (cutoff == null || a.SubmittedAt <= cutoff))
                .GroupBy(a => new { a.AttackerParticipationId, a.SubmittedAtRound })
                .Select(g => new { g.Key.AttackerParticipationId, g.Key.SubmittedAtRound, Points = g.Sum(a => a.Points) })
                .ToListAsync(token))
            .ToDictionary(x => (x.AttackerParticipationId, x.SubmittedAtRound), x => x.Points);

        var capsByTeamRound = (await Context.AdAttacks
                .Where(a => partIds.Contains(a.VictimParticipationId) && (cutoff == null || a.SubmittedAt <= cutoff))
                .GroupBy(a => new { a.VictimParticipationId, a.ChallengeId, a.SubmittedAtRound })
                .Select(g => new { g.Key.VictimParticipationId, g.Key.ChallengeId, g.Key.SubmittedAtRound, Count = g.Count() })
                .ToListAsync(token))
            .GroupBy(x => (x.VictimParticipationId, x.SubmittedAtRound))
            .ToDictionary(g => g.Key, g => g.Select(x => (x.ChallengeId, x.Count)).ToList());

        var slaByTeamRound = (await Context.AdCheckResults
                .Where(c => cutoff == null || c.CheckedAt <= cutoff)
                .Join(Context.AdTeamServices, c => c.AdTeamServiceId, ts => ts.Id,
                    (c, ts) => new { c.AdRoundId, c.SlaCredit, ts.ParticipationId })
                .Where(x => partIds.Contains(x.ParticipationId))
                .Join(Context.AdRounds, x => x.AdRoundId, r => r.Id,
                    (x, r) => new { x.ParticipationId, x.SlaCredit, r.Number, r.GameId })
                .Where(x => x.GameId == gameId)
                .GroupBy(x => new { x.ParticipationId, x.Number })
                .Select(g => new { g.Key.ParticipationId, g.Key.Number, Credit = g.Sum(x => x.SlaCredit) })
                .ToListAsync(token))
            .ToDictionary(x => (x.ParticipationId, x.Number), x => x.Credit);

        // KotH hold credit per (team, round) — without this the chart line for any
        // team with KotH points diverges from the scoreboard total (Scoreboard adds
        // tKoth at line 240; the chart historically didn't). Join through AdRound
        // to translate the result's AdRoundId into a round Number, so the cumulative
        // axis lines up exactly with cumAttack / cumSla below.
        var kothByTeamRound = (await Context.KothControlResults
                .Where(r => r.GameId == gameId && r.ControllingParticipationId != null
                    && (cutoff == null || r.CheckedAt <= cutoff))
                .Join(Context.AdRounds, r => r.AdRoundId, ar => ar.Id,
                    (r, ar) => new { Pid = r.ControllingParticipationId!.Value, ar.Number, Delta = r.HoldCredit - r.Penalty })
                .Where(x => partIds.Contains(x.Pid))
                .GroupBy(x => new { x.Pid, x.Number })
                .Select(g => new { g.Key.Pid, g.Key.Number, Delta = g.Sum(x => x.Delta) })
                .ToListAsync(token))
            .ToDictionary(x => (x.Pid, x.Number), x => x.Delta);

        // Emit at most MaxTimelinePoints points per team (downsample the rounds).
        var emitEvery = Math.Max(1, (int)Math.Ceiling(rounds.Count / (double)MaxTimelinePoints));

        foreach (var team in teams)
        {
            var tl = new AdTeamTimeline
            {
                ParticipationId = team.Id,
                TeamId = team.TeamId,
                TeamName = team.Team.Name,
                Division = team.Division?.Name,
            };

            double cumAttack = 0, cumSla = 0, cumKoth = 0;
            var cumCaps = new Dictionary<int, int>();

            for (var i = 0; i < rounds.Count; i++)
            {
                var round = rounds[i];
                cumAttack += atkByTeamRound.GetValueOrDefault((team.Id, round.Number), 0);
                cumSla += slaByTeamRound.GetValueOrDefault((team.Id, round.Number), 0);
                cumKoth += kothByTeamRound.GetValueOrDefault((team.Id, round.Number), 0);
                if (capsByTeamRound.TryGetValue((team.Id, round.Number), out var caps))
                    foreach (var (ch, cnt) in caps)
                        cumCaps[ch] = cumCaps.GetValueOrDefault(ch) + cnt;

                // Only materialize a point at each downsample boundary (and the last round).
                if (i % emitEvery != 0 && i != rounds.Count - 1)
                    continue;

                var defenseLoss = cumCaps.Values.Sum(AdScoring.DefenseLoss);
                tl.Items.Add(new AdTimelinePoint
                {
                    Round = round.Number,
                    Time = round.EndsAt,
                    // Same shape as Scoreboard's Total = tAttack + tSla - tDefense + tKoth.
                    Score = cumAttack + AdScoring.SlaPoints(cumSla, activeTeams) - defenseLoss + cumKoth
                });
            }

            result.Teams.Add(tl);
        }

        return result;
    }
}
