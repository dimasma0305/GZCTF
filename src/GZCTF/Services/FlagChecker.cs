using System.Threading.Channels;
using GZCTF.Models.Internal;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GZCTF.Services;

public class FlagChecker(
    ChannelReader<Submission> channelReader,
    ChannelWriter<Submission> channelWriter,
    ILogger<FlagChecker> logger,
    IServiceScopeFactory serviceScopeFactory) : IHostedService
{
    private const int MaxWorkerCount = 4;
    private CancellationTokenSource TokenSource { get; set; } = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        TokenSource = new CancellationTokenSource();

        for (var i = 0; i < GetWorkerCount(); ++i)
        {
            await Task.Factory.StartNew(() => Checker(i, TokenSource.Token), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        await using var scope = serviceScopeFactory.CreateAsyncScope();

        var submissionRepository = scope.ServiceProvider.GetRequiredService<ISubmissionRepository>();
        var flags = await submissionRepository.GetUncheckedFlags(TokenSource.Token);

        foreach (var item in flags)
            await channelWriter.WriteAsync(item, TokenSource.Token);

        if (flags.Length > 0)
            logger.SystemLog(StaticLocalizer[nameof(Resources.Program.FlagsChecker_Recheck), flags.Length],
                TaskStatus.Pending,
                LogLevel.Debug);

        logger.SystemLog(StaticLocalizer[nameof(Resources.Program.FlagsChecker_Started)], TaskStatus.Success,
            LogLevel.Debug);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        TokenSource.Cancel();

        logger.SystemLog(StaticLocalizer[nameof(Resources.Program.FlagsChecker_Stopped)], TaskStatus.Exit,
            LogLevel.Debug);

        return Task.CompletedTask;
    }

    internal static int GetWorkerCount()
    {
        // if RAM < 2GiB or CPU <= 3, return 1
        // if RAM < 4GiB or CPU <= 6, return 2
        // otherwise, return 4
        var memoryInfo = GC.GetGCMemoryInfo();
        var freeMemory = memoryInfo.TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;
        var cpuCount = Environment.ProcessorCount;

        if (freeMemory < 2 || cpuCount <= 3)
            return 1;
        if (freeMemory < 4 || cpuCount <= 6)
            return 2;
        return MaxWorkerCount;
    }

    private async Task Checker(int id, CancellationToken token = default)
    {
        logger.SystemLog(StaticLocalizer[nameof(Resources.Program.FlagsChecker_WorkerStarted), id],
            TaskStatus.Pending,
            LogLevel.Debug);

        try
        {
            await foreach (var item in channelReader.ReadAllAsync(token))
            {
                logger.SystemLog(
                    StaticLocalizer[nameof(Resources.Program.FlagsChecker_WorkerStartProcessing), id,
                        item.Answer],
                    TaskStatus.Pending, LogLevel.Debug);

                await using var scope = serviceScopeFactory.CreateAsyncScope();

                var cacheHelper = scope.ServiceProvider.GetRequiredService<CacheHelper>();
                var eventRepository =
                    scope.ServiceProvider.GetRequiredService<IGameEventRepository>();
                var instanceRepository =
                    scope.ServiceProvider.GetRequiredService<IGameInstanceRepository>();
                var gameNoticeRepository =
                    scope.ServiceProvider.GetRequiredService<IGameNoticeRepository>();
                var submissionRepository =
                    scope.ServiceProvider.GetRequiredService<ISubmissionRepository>();

                try
                {
                    var (type, ans) = await instanceRepository.VerifyAnswer(item, token);

                    switch (ans)
                    {
                        case AnswerResult.NotFound:
                            logger.Log(
                                StaticLocalizer[nameof(Resources.Program.FlagChecker_UnknownInstance),
                                    item.TeamName,
                                    item.ChallengeName],
                                item.User,
                                TaskStatus.NotFound, LogLevel.Warning);
                            break;
                        case AnswerResult.Accepted:
                            {
                                logger.Log(
                                    StaticLocalizer[nameof(Resources.Program.FlagChecker_AnswerAccepted),
                                        item.TeamName,
                                        item.ChallengeName,
                                        item.Answer],
                                    item.User, TaskStatus.Success, LogLevel.Information);

                                await eventRepository.AddEvent(
                                    GameEvent.FromSubmission(item, type, ans, StaticLocalizer), token);

                                // always flush the scoreboard
                                await cacheHelper.FlushScoreboardCache(item.GameId, token);

                                // Access-event-based cheat checks. Best-effort: must never
                                // block accept-path side effects, which have already run.
                                // Skipped once the game has ended: cheat correlation is a
                                // live-game concern, and post-game practice solves (now real
                                // Accepted submissions via the practice FlagContext) must not
                                // add suspicion to the just-ended game's report.
                                if (item.Game!.EndTimeUtc > DateTimeOffset.UtcNow)
                                {
                                    try
                                    {
                                        var detector = scope.ServiceProvider
                                            .GetRequiredService<IContainerAccessSubmissionDetector>();
                                        var providerOptions = scope.ServiceProvider
                                            .GetRequiredService<IOptions<ContainerProvider>>().Value;
                                        var platformProxyEnabled =
                                            providerOptions.PortMappingType == ContainerPortMappingType.PlatformProxy;
                                        await detector.RunChecks(item, platformProxyEnabled, token);
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.LogError(ex,
                                            "ContainerAccessSubmissionDetector failed for submission {Id}",
                                            item.Id);
                                    }
                                }
                                break;
                            }
                        default:
                            {
                                logger.Log(
                                    StaticLocalizer[nameof(Resources.Program.FlagChecker_AnswerRejected),
                                        item.TeamName,
                                        item.ChallengeName,
                                        item.Answer],
                                    item.User, TaskStatus.Failed, LogLevel.Information);

                                await eventRepository.AddEvent(
                                    GameEvent.FromSubmission(item, type, ans, StaticLocalizer), token);

                                // Flag-sharing (StolenFlag) detection is a live-game concern.
                                // Skip it once the game has ended so a post-game practice solve
                                // (submitting another team's old dynamic flag) can't add cheat
                                // suspicion to the just-ended game's report — matching the
                                // ContainerAccessSubmissionDetector gate above.
                                var result = item.Game!.EndTimeUtc > DateTimeOffset.UtcNow
                                    ? await instanceRepository.CheckCheat(item, token)
                                    : new CheatCheckInfo();
                                ans = result.AnswerResult;

                                if (ans == AnswerResult.CheatDetected)
                                {
                                    logger.Log(
                                        StaticLocalizer[nameof(Resources.Program.FlagChecker_CheatDetected),
                                            item.TeamName,
                                            item.ChallengeName,
                                            result.SourceTeamName ?? ""],
                                        item.User, TaskStatus.Success, LogLevel.Information);

                                    await eventRepository.AddEvent(
                                        new()
                                        {
                                            Type = EventType.CheatDetected,
                                            Values =
                                                [item.ChallengeName, item.TeamName, result.SourceTeamName ?? ""],
                                            TeamId = item.TeamId,
                                            UserId = item.UserId,
                                            GameId = item.GameId
                                        }, token);
                                }

                                break;
                            }
                    }

                    // Blood notices ("X drew first blood on C") reveal late-game standings movement.
                    // During the ICPC freeze window they must NOT be pushed to non-monitors — mirror
                    // the freeze gate the attack feeds apply. PERSIST the notice (so it's in the
                    // post-game feed + monitor view + the final board derivation) but SUPPRESS the
                    // live broadcast during freeze. The public /Notices endpoint additionally hides
                    // in-freeze blood notices from non-monitors until the game ends, so polling can't
                    // leak them either. (Persist-but-don't-broadcast matches the AdSnapshot pattern;
                    // skipping AddNotice entirely would lose the notice from the feed permanently.)
                    var noticeNow = DateTimeOffset.UtcNow;
                    var inFreeze = item.Game!.FreezeTimeUtc is { } freeze
                                   && noticeNow >= freeze && noticeNow < item.Game.EndTimeUtc;
                    if (item.Game!.EndTimeUtc > noticeNow
                        && type != SubmissionType.Unaccepted
                        && type != SubmissionType.Normal)
                        await gameNoticeRepository.AddNotice(
                            GameNotice.FromSubmission(item, type, StaticLocalizer), broadcast: !inFreeze, token);

                    item.Status = ans;
                    await submissionRepository.SendSubmission(item);

                    // Public attack-animation feed: always broadcast, with
                    // the precise SubmissionType resolved by VerifyAnswer
                    // (Normal / FirstBlood / SecondBlood / ThirdBlood /
                    // Unaccepted). The AttackHub is a separate, unauth'd
                    // broadcast channel and doesn't affect the monitor feed.
                    await submissionRepository.SendAttackEvent(item, type);
                }
                catch (DbUpdateConcurrencyException)
                {
                    logger.SystemLog(
                        StaticLocalizer[nameof(Resources.Program.FlagChecker_ConcurrencyFailed), item.Id],
                        TaskStatus.Failed,
                        LogLevel.Warning);
                    await channelWriter.WriteAsync(item, token);
                }
                catch (Exception e)
                {
                    logger.SystemLog(
                        StaticLocalizer[nameof(Resources.Program.FlagsChecker_WorkerExceptionOccurred), id],
                        TaskStatus.Failed,
                        LogLevel.Debug);
                    logger.LogErrorMessage(e);
                }

                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            logger.SystemLog(StaticLocalizer[nameof(Resources.Program.FlagsChecker_WorkerCancelled), id],
                TaskStatus.Exit,
                LogLevel.Debug);
        }
        finally
        {
            logger.SystemLog(StaticLocalizer[nameof(Resources.Program.FlagsChecker_WorkerStopped), id],
                TaskStatus.Exit,
                LogLevel.Debug);
        }
    }
}
