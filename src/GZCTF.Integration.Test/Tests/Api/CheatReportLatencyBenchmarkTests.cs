using System.Diagnostics;
using System.Net.Http.Json;
using GZCTF.Integration.Test.Base;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GZCTF.Integration.Test.Tests.Api;

/// <summary>
/// Controlled latency benchmark for GET /api/game/{id}/cheatreport.
///
/// Seeds a self-contained game at a given team scale (each team with its own
/// solve sequence over a shared challenge pool, plus login logs so the
/// identity-correlation path does real work), then measures the endpoint's
/// response-time distribution: 1 cold request (first-ever report for the game,
/// includes idempotent SuspicionEvent persistence), 3 discarded warm-up
/// requests, and 30 timed warm requests.
///
/// Output (per scale): cold, median, p95, max in milliseconds — written via
/// ITestOutputHelper as a single parseable line:
///   BENCH game=… teams=… subs=… cold=…ms median=…ms p95=…ms max=…ms
///
/// The pairwise similarity stage is O(T²) in team count, so the scales below
/// (12 → 24 → 48) double the team count to expose the growth curve.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class CheatReportLatencyBenchmarkTests(GZCTFApplicationFactory factory, ITestOutputHelper output)
{
    const int ChallengeCount = 10;
    const int WarmupRequests = 3;
    const int TimedRequests = 30;

    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(48)]
    public async Task GetCheatReport_LatencyAtScale(int teamCount)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 1. Self-contained game + shared challenge pool
        var game = await TestDataSeeder.CreateGameAsync(factory.Services,
            $"Latency Bench {teamCount}T " + TestDataSeeder.RandomName());

        var challenges = new List<TestDataSeeder.SeededChallenge>(ChallengeCount);
        for (var c = 0; c < ChallengeCount; c++)
            challenges.Add(await TestDataSeeder.CreateStaticChallengeAsync(
                factory.Services, game.Id, $"BC{c}", $"flag{{bench_{c}}}"));

        // 2. Teams with deterministic, distinct solve sequences + login logs
        var baseTime = DateTimeOffset.UtcNow.AddHours(-2);
        var submissionCount = 0;

        for (var t = 0; t < teamCount; t++)
        {
            var user = await TestDataSeeder.CreateUserAsync(
                factory.Services, TestDataSeeder.RandomName(), "Test@123");
            var team = await TestDataSeeder.CreateTeamAsync(
                factory.Services, user.Id, $"Bench Team {t} " + TestDataSeeder.RandomName());
            var part = await TestDataSeeder.JoinGameAsync(
                factory.Services, game.Id, team.Id, user.Id);

            // Login log: distinct IP per team so the IP-correlation stage scans real rows
            await context.Logs.AddAsync(new LogModel
            {
                Level = "Info",
                Logger = "AccountController",
                Message = "Login",
                TimeUtc = baseTime.AddMinutes(t),
                UserName = user.UserName,
                RemoteIP = System.Net.IPAddress.Parse($"10.{t / 250}.{t % 250}.7")
            });

            // Each team solves 6-8 challenges, rotated start + stride so the
            // sequences differ and the LCS/Jaccard stage does real comparisons.
            var solves = 6 + t % 3;
            for (var s = 0; s < solves; s++)
            {
                var chal = challenges[(t + s * (1 + t % 3)) % ChallengeCount];
                await context.Submissions.AddAsync(new Submission
                {
                    GameId = game.Id,
                    ChallengeId = chal.Id,
                    TeamId = team.Id,
                    ParticipationId = part.Id,
                    UserId = user.Id,
                    Answer = "flag",
                    Status = AnswerResult.Accepted,
                    SubmitTimeUtc = baseTime.AddMinutes(5 + t + s * (3 + t % 5))
                });
                submissionCount++;
            }

            // One wrong attempt per team so ZeroWrongAttempts paths stay realistic
            await context.Submissions.AddAsync(new Submission
            {
                GameId = game.Id,
                ChallengeId = challenges[t % ChallengeCount].Id,
                TeamId = team.Id,
                ParticipationId = part.Id,
                UserId = user.Id,
                Answer = "wrong",
                Status = AnswerResult.WrongAnswer,
                SubmitTimeUtc = baseTime.AddMinutes(4 + t)
            });
            submissionCount++;
        }

        await context.SaveChangesAsync();

        // 3. Monitor client
        var monitor = await TestDataSeeder.CreateUserAsync(
            factory.Services, TestDataSeeder.RandomName(), "Test@123", role: Role.Admin);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/Account/Login",
            new { UserName = monitor.UserName, Password = "Test@123" });

        var url = $"/api/game/{game.Id}/cheatreport";
        var sw = new Stopwatch();

        // 4. Cold request: first report ever for this game (persists SuspicionEvents)
        sw.Restart();
        var cold = await client.GetAsync(url);
        sw.Stop();
        cold.EnsureSuccessStatusCode();
        var coldMs = sw.Elapsed.TotalMilliseconds;

        // 5. Warm-up (discarded)
        for (var i = 0; i < WarmupRequests; i++)
            (await client.GetAsync(url)).EnsureSuccessStatusCode();

        // 6. Timed warm requests
        var timings = new List<double>(TimedRequests);
        for (var i = 0; i < TimedRequests; i++)
        {
            sw.Restart();
            var resp = await client.GetAsync(url);
            sw.Stop();
            resp.EnsureSuccessStatusCode();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var median = timings[timings.Count / 2];
        var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
        var max = timings[^1];

        output.WriteLine(
            $"BENCH game={game.Id} teams={teamCount} subs={submissionCount} " +
            $"cold={coldMs:F0}ms median={median:F0}ms p95={p95:F0}ms max={max:F0}ms");

        // Sanity bound: warm median must stay interactive even at the largest scale
        Assert.True(median < 10_000, $"median {median:F0}ms exceeds 10s at {teamCount} teams");
    }
}
