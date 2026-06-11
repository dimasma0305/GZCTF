using GZCTF.Models.Request.Info;
using GZCTF.Repositories.Interface;
using GZCTF.Services;
using GZCTF.Services.Cache;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Repositories;

public class TeamRepository(
    ILogger<TeamRepository> logger,
    CacheHelper cacheHelper,
    IParticipationRepository participationRepository,
    AdContainerManager adContainerManager,
    AppDbContext context) : RepositoryBase(context), ITeamRepository
{
    public async Task<bool> AnyActiveGame(Team team, CancellationToken token = default)
    {
        var current = DateTimeOffset.UtcNow;
        var result = await Context.Participations
            .Where(p => p.Team == team && p.Game.EndTimeUtc > current)
            .AnyAsync(token);

        if (!team.Locked || result)
            return result;

        team.Locked = false;
        await SaveAsync(token);

        return result;
    }

    public override Task<int> CountAsync(CancellationToken token = default) => Context.Teams.CountAsync(token);

    public Task<bool> CheckIsCaptain(UserInfo user, CancellationToken token = default) =>
        Context.Teams.AnyAsync(t => t.Captain == user, token);

    public async Task<Team> CreateTeam(TeamUpdateModel model, UserInfo user, CancellationToken token = default)
    {
        if (model.Name is null)
            throw new ArgumentNullException(nameof(model.Name).ToLower());

        Team team = new() { Name = model.Name, Captain = user, Bio = model.Bio };

        team.Members.Add(user);

        await Context.AddAsync(team, token);
        await SaveAsync(token);

        return team;
    }

    public async Task DeleteTeam(Team team, CancellationToken token = default)
    {
        // Load the team's participations (with their writeup blob) so we can reap per-team
        // resources BEFORE the cascade deletes the rows the teardown keys off. Mirrors
        // GameRepository.DeleteGame, which had this teardown while DeleteTeam was a bare cascade
        // that orphaned live A&D containers + host flag files and leaked writeup PDFs.
        var parts = await Context.Participations
            .Where(p => p.TeamId == team.Id)
            .Include(p => p.Writeup)
            .ToListAsync(token);

        // Tear down A&D service containers (+ host flag-mount files) per participation, BEFORE the
        // cascade — once the AdTeamService rows are gone the reconciler can't find the containers
        // (they'd run until game end) and the host flag file would never be cleaned. Per-team only:
        // KotH hills are shared per challenge, so they stay up for the remaining teams. Best-effort:
        // a container-daemon hiccup must not block the delete (matches DeleteGame).
        foreach (var part in parts)
        {
            try
            {
                await adContainerManager.DestroyContainersForParticipationAsync(part.Id, token);
            }
            catch (Exception e)
            {
                logger.SystemLog(
                    $"A&D container teardown during team delete failed (continuing): participation={part.Id}: {e.Message}",
                    TaskStatus.Failed, LogLevel.Warning);
            }
        }

        var gameIds = parts.Select(p => p.GameId).Distinct().ToList();

        var trans = await BeginTransactionAsync(token);
        try
        {
            // RemoveParticipation deletes each participation's writeup blob (ref-count) + row,
            // instead of letting the bare team cascade orphan the blob on disk.
            foreach (var part in parts)
                await participationRepository.RemoveParticipation(part, false, token);

            Context.Remove(team);
            await SaveAsync(token);
            await trans.CommitAsync(token);
        }
        catch
        {
            await trans.RollbackAsync(token);
            throw;
        }

        // Flush scoreboard caches for every game the team was in — otherwise the deleted team's
        // row lingers on the board for up to 7 days. A&D/KotH families don't auto-regenerate once
        // a game is paused/ended, so flush them (IncludingFrozen) when the game uses that engine.
        await FlushScoreboardsForGames(gameIds, token);
    }

    public async Task FlushScoreboardCacheForTeam(int teamId, CancellationToken token = default)
    {
        var gameIds = await Context.Participations
            .Where(p => p.TeamId == teamId)
            .Select(p => p.GameId)
            .Distinct()
            .ToListAsync(token);
        await FlushScoreboardsForGames(gameIds, token);
    }

    private async Task FlushScoreboardsForGames(IReadOnlyCollection<int> gameIds, CancellationToken token)
    {
        foreach (var gameId in gameIds)
        {
            await cacheHelper.FlushScoreboardCache(gameId, token);
            if (await Context.GameChallenges.AnyAsync(
                    c => c.GameId == gameId
                         && (c.Type == ChallengeType.AttackDefense || c.Type == ChallengeType.KingOfTheHill),
                    token))
                await cacheHelper.FlushAdScoreboardCacheIncludingFrozen(gameId, token);
        }
    }

    public Task<Team?> GetTeamById(int id, CancellationToken token = default) =>
        Context.Teams.Include(e => e.Members)
            .FirstOrDefaultAsync(t => t.Id == id, token);

    public Task<Team[]> GetTeams(int count = 100, int skip = 0, CancellationToken token = default) =>
        Context.Teams.Include(t => t.Members).OrderBy(t => t.Id)
            .Skip(skip).Take(count).ToArrayAsync(token);

    public Task<Team[]> GetUserTeams(UserInfo user, CancellationToken token = default) =>
        Context.Teams.Where(t => t.Members.Any(u => u.Id == user.Id))
            .Include(t => t.Members).ToArrayAsync(token);

    public Task<Team[]> SearchTeams(string hint, CancellationToken token = default)
    {
        var loweredHint = hint.ToLower();
        var query = int.TryParse(hint, out var id)
            ? Context.Teams.Include(t => t.Members)
                .Where(item => item.Name.ToLower().Contains(loweredHint) || item.Id == id)
            : Context.Teams.Include(t => t.Members).Where(item => item.Name.ToLower().Contains(loweredHint));

        return query.OrderBy(t => t.Id).Take(30).ToArrayAsync(token);
    }

    public Task Transfer(Team team, UserInfo user, CancellationToken token = default)
    {
        team.Captain = user;
        return SaveAsync(token);
    }

    public async Task<bool> VerifyToken(int id, string inviteCode, CancellationToken token = default)
    {
        var team = await Context.Teams.FirstOrDefaultAsync(t => t.Id == id, token);
        return team is not null && team.InviteCode == inviteCode;
    }
}
