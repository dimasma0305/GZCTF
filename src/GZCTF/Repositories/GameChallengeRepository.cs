using GZCTF.Models.Request.Edit;
using GZCTF.Repositories.Interface;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Repositories;

public class GameChallengeRepository(
    AppDbContext context,
    IBlobRepository blobRepository,
    IContainerRepository containerRepository
) : RepositoryBase(context),
    IGameChallengeRepository
{
    public async Task AddFlags(GameChallenge challenge, FlagCreateModel[] models, CancellationToken token = default)
    {
        foreach (var model in models)
        {
            var attachment = model.ToAttachment(await blobRepository.GetBlobByHash(model.FileHash, token));

            challenge.Flags.Add(new() { Flag = model.Flag, Challenge = challenge, Attachment = attachment });
        }

        await SaveAsync(token);
    }

    public async Task<GameChallenge> CreateChallenge(Game game, GameChallenge challenge,
        CancellationToken token = default)
    {
        await Context.AddAsync(challenge, token);
        game.Challenges.Add(challenge);
        await SaveAsync(token);
        return challenge;
    }

    public async Task<bool> EnsureInstances(GameChallenge challenge, Game game, CancellationToken token = default)
    {
        var newInstances = Context.Participations
            .Where(p => p.GameId == game.Id && !Context.Set<GameInstance>()
                .Where(gi => gi.ChallengeId == challenge.Id)
                .Select(gi => gi.ParticipationId).Contains(p.Id)
            )
            .Select(p => new GameInstance { ParticipationId = p.Id, ChallengeId = challenge.Id })
            .ToList();

        if (newInstances.Count == 0)
            return false;

        await Context.Set<GameInstance>().AddRangeAsync(newInstances, token);
        await SaveAsync(token);

        return true;
    }

    public Task<GameChallenge?> GetChallenge(int gameId, int id, CancellationToken token = default)
        => Context.GameChallenges
            .Where(c => c.Id == id && c.GameId == gameId).FirstOrDefaultAsync(token);

    public Task LoadFlags(GameChallenge challenge, CancellationToken token = default) =>
        Context.Entry(challenge).Collection(c => c.Flags).LoadAsync(token);

    public Task<GameChallenge[]> GetChallenges(int gameId, CancellationToken token = default) =>
        Context.GameChallenges
            .Where(c => c.GameId == gameId).OrderBy(c => c.Id).ToArrayAsync(token);

    public Task<GameChallenge[]> GetChallengesWithTrafficCapturing(int gameId, CancellationToken token = default) =>
        Context.GameChallenges.IgnoreAutoIncludes().Where(c => c.GameId == gameId && c.EnableTrafficCapture)
            .ToArrayAsync(token);

    public async Task RemoveChallenge(GameChallenge challenge, bool save = true, CancellationToken token = default)
    {
        // Destroy any running containers BEFORE deleting the rows — both per-team (linked via
        // GameInstance) and the shared one (GameChallenge.SharedContainerId). RemoveChallenge is
        // the common sink for per-challenge delete (EditController) and whole-game delete
        // (GameRepository.DeleteGame), so doing it here covers both. Otherwise RemoveChallenge
        // only deletes DB rows and the real Docker/K8s containers run orphaned until the idle
        // cron reaps them. (A&D/KotH containers are torn down separately by AdContainerManager.)
        if (challenge.Type.IsContainer())
        {
            foreach (var container in await Context.GameInstances
                         .Where(i => i.ChallengeId == challenge.Id && i.ContainerId != null)
                         .Select(i => i.Container)
                         .ToListAsync(token))
                if (container is not null)
                    await containerRepository.DestroyContainer(container, token);

            if (challenge.SharedContainerId is { } sharedId)
            {
                var shared = await Context.Containers.FirstOrDefaultAsync(c => c.Id == sharedId, token);
                if (shared is not null)
                    await containerRepository.DestroyContainer(shared, token);
            }
        }

        await blobRepository.DeleteAttachment(challenge.Attachment, token);

        await LoadFlags(challenge, token);

        // only dynamic attachment challenge's flag contexts have attachment
        if (challenge.Type == ChallengeType.DynamicAttachment)
            foreach (var flag in challenge.Flags)
                await blobRepository.DeleteAttachment(flag.Attachment, token);

        Context.RemoveRange(challenge.Flags);
        Context.Remove(challenge);

        if (save)
            await SaveAsync(token);
    }

    public async Task<TaskStatus> RemoveFlag(GameChallenge challenge, int flagId, CancellationToken token = default)
    {
        var flag = await Context.FlagContexts
            .FirstOrDefaultAsync(f => f.Challenge == challenge && f.Id == flagId, token);

        if (flag is null)
            return TaskStatus.NotFound;

        await blobRepository.DeleteAttachment(flag.Attachment, token);

        Context.Remove(flag);

        await SaveAsync(token);

        // If there are no more flags, disable the challenge
        if (!await Context.FlagContexts.AnyAsync(f => f.Challenge == challenge, token))
        {
            challenge.IsEnabled = false;
            await SaveAsync(token);
        }

        return TaskStatus.Success;
    }

    public async Task UpdateAttachment(GameChallenge challenge, AttachmentCreateModel model,
        CancellationToken token = default)
    {
        var attachment = model.ToAttachment(await blobRepository.GetBlobByHash(model.FileHash, token));

        await blobRepository.DeleteAttachment(challenge.Attachment, token);

        if (attachment is not null)
            await Context.AddAsync(attachment, token);

        challenge.Attachment = attachment;

        await SaveAsync(token);
    }
}
