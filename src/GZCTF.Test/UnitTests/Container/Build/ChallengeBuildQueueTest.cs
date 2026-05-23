using System.Linq;
using System.Threading.Channels;
using GZCTF.Services.Container.Build;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Container.Build;

/// <summary>
/// Dedup + lifecycle coverage for the build queue. Tests use a real
/// bounded <see cref="Channel{T}"/> per-test so the production code
/// path (TryWrite, drain by reader) runs unchanged; we just don't
/// stand up the BackgroundService.
/// </summary>
public class ChallengeBuildQueueTest
{
    private static ChallengeBuildJob Job(int challengeId, int attempt = 1) => new(
        ChallengeId: challengeId,
        GameId: 1,
        Slug: $"chal-{challengeId}",
        ContextDir: "/tmp/ignored",
        Dockerfile: "Dockerfile",
        Trigger: BuildTrigger.Manual,
        Attempt: attempt);

    private static (ChallengeBuildQueue queue, ChannelReader<ChallengeBuildJob> reader) Build(int capacity = 8)
    {
        var ch = Channel.CreateBounded<ChallengeBuildJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
        });
        return (new ChallengeBuildQueue(ch.Writer), ch.Reader);
    }

    [Fact]
    public void Enqueue_FirstCall_ReturnsEnqueued()
    {
        var (q, _) = Build();
        Assert.Equal(EnqueueResult.Enqueued, q.Enqueue(Job(1)));
        Assert.True(q.IsPending(1));
    }

    [Fact]
    public void Enqueue_DuplicateChallengeId_ReturnsAlreadyPending()
    {
        var (q, reader) = Build();
        q.Enqueue(Job(1));
        var second = q.Enqueue(Job(1));

        Assert.Equal(EnqueueResult.AlreadyPending, second);
        // Channel got only ONE job — dedup did not double-write.
        Assert.True(reader.TryRead(out _));
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void Enqueue_DifferentChallengeIds_BothAccepted()
    {
        var (q, _) = Build();
        Assert.Equal(EnqueueResult.Enqueued, q.Enqueue(Job(1)));
        Assert.Equal(EnqueueResult.Enqueued, q.Enqueue(Job(2)));
        Assert.True(q.IsPending(1));
        Assert.True(q.IsPending(2));
    }

    [Fact]
    public void MarkEnd_ClearsPendingState_AllowsReEnqueue()
    {
        var (q, _) = Build();
        q.Enqueue(Job(1));
        Assert.True(q.IsPending(1));

        // Reflection access — MarkEnd is internal but available via
        // InternalsVisibleTo("GZCTF.Test").
        q.MarkEnd(1);

        Assert.False(q.IsPending(1));
        Assert.Equal(EnqueueResult.Enqueued, q.Enqueue(Job(1)));
    }

    [Fact]
    public void TryRetry_AlwaysBypassesDedup()
    {
        var (q, reader) = Build();
        q.Enqueue(Job(1));
        reader.TryRead(out _); // worker pulls original

        // Retry from worker — challenge is still "pending" per dedup,
        // but TryRetry must still succeed (transient-failure backoff path).
        Assert.True(q.TryRetry(Job(1, attempt: 2)));
        Assert.True(reader.TryRead(out var retried));
        Assert.Equal(2, retried.Attempt);
    }

    [Fact]
    public void MarkAttemptDoneRetrying_KeepsDedupSticky_BlocksOperatorEnqueue()
    {
        var (q, _) = Build();
        q.Enqueue(Job(1));

        // Worker finished attempt 1, about to backoff before attempt 2.
        // Dedup must stay sticky so operator clicking Build now is no-op.
        q.MarkAttemptDoneRetrying(1);

        Assert.True(q.IsPending(1));
        Assert.Equal(EnqueueResult.AlreadyPending, q.Enqueue(Job(1)));
    }

    [Fact]
    public void Enqueue_OnFullChannel_RollsBackDedup()
    {
        // Capacity 1 + Wait mode → second TryWrite returns false
        // immediately (BoundedChannelFullMode.Wait is for WriteAsync;
        // TryWrite always returns false when full).
        var (q, _) = Build(capacity: 1);
        Assert.Equal(EnqueueResult.Enqueued, q.Enqueue(Job(1)));

        var rejected = q.Enqueue(Job(2));

        Assert.Equal(EnqueueResult.Rejected, rejected);
        // Critical: the failed enqueue must NOT leave Challenge 2 in
        // the dedup set, or operators would never be able to retry it.
        Assert.False(q.IsPending(2));
    }
}
