using System.Collections.Generic;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Edit;
using GZCTF.Services.Transfer;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Transfer;

/// <summary>
/// Round-trip tests for the push-back serializer. The serializer
/// must produce yaml that re-parses to equivalent in-memory state via
/// the existing <see cref="ChallengeImportService"/> path; otherwise
/// every push-back would corrupt the upstream repo's challenge.yml.
/// </summary>
public class ChallengeYamlSerializerTest
{
    private static GameChallenge StaticAttachment() => new()
    {
        Title = "Sample",
        Content = "Markdown description.",
        Category = ChallengeCategory.Web,
        Type = ChallengeType.StaticAttachment,
    };

    [Fact]
    public void Serialize_StaticAttachment_EmitsCoreFields()
    {
        var yaml = ChallengeYamlSerializer.Serialize(StaticAttachment(), ["flag{abc}"]);

        Assert.Contains("name: Sample", yaml);
        Assert.Contains("type: StaticAttachment", yaml);
        Assert.Contains("category: Web", yaml);
        Assert.Contains("flag{abc}", yaml);
        // container section MUST NOT appear for non-container types
        Assert.DoesNotContain("container:", yaml);
    }

    [Fact]
    public void Serialize_StaticContainer_EmitsContainerSection()
    {
        var ch = StaticAttachment();
        ch.Type = ChallengeType.StaticContainer;
        ch.ContainerImage = "registry.example.com/app:1";
        ch.MemoryLimit = 256;
        ch.CPUCount = 2;
        ch.StorageLimit = 1024;
        ch.ExposePort = 1337;

        var yaml = ChallengeYamlSerializer.Serialize(ch, ["flag{c}"]);

        Assert.Contains("container:", yaml);
        Assert.Contains("containerImage: registry.example.com/app:1", yaml);
        Assert.Contains("memoryLimit: 256", yaml);
        Assert.Contains("cpuCount: 2", yaml);
        Assert.Contains("storageLimit: 1024", yaml);
        Assert.Contains("exposePort: 1337", yaml);
    }

    [Fact]
    public void Serialize_DynamicContainer_OmitsMaterializedFlags()
    {
        var ch = StaticAttachment();
        ch.Type = ChallengeType.DynamicContainer;
        ch.FlagTemplate = "flag{[GUID]}";
        ch.ContainerImage = "img:1";

        // Caller of Serialize passes flagTexts=[] for dynamic — the
        // FlagTemplate field carries the meaning, not the rendered list.
        var yaml = ChallengeYamlSerializer.Serialize(ch, []);

        Assert.Contains("type: DynamicContainer", yaml);
        Assert.Contains("flagTemplate: flag{[GUID]}", yaml);
        // No raw `flags:` block since flagTexts was empty.
        Assert.DoesNotContain("flags:", yaml);
    }

    [Theory]
    [InlineData("Author: **alice**\n\nbody here", "alice", "body here")]
    [InlineData("Author: **bob**\n\nline1\nline2", "bob", "line1\nline2")]
    public void Serialize_AuthorPrefix_StrippedAndEmittedSeparately(string content, string expectedAuthor, string expectedDescription)
    {
        var ch = StaticAttachment();
        ch.Content = content;

        var yaml = ChallengeYamlSerializer.Serialize(ch, []);

        Assert.Contains($"author: {expectedAuthor}", yaml);
        // The stripped body lands as description; the "Author: **..." line is gone.
        Assert.DoesNotContain("Author: **", yaml);
        // The body content (at least the first line) is preserved.
        var firstLine = expectedDescription.Split('\n')[0];
        Assert.Contains(firstLine, yaml);
    }

    [Fact]
    public void Serialize_NoAuthorPrefix_NoAuthorField()
    {
        var ch = StaticAttachment();
        ch.Content = "just a description with no author block";

        var yaml = ChallengeYamlSerializer.Serialize(ch, []);

        Assert.DoesNotContain("author:", yaml);
        Assert.Contains("just a description", yaml);
    }

    [Fact]
    public void Serialize_PartialAuthorPrefix_LeavesContentUntouched()
    {
        var ch = StaticAttachment();
        // Starts with "Author: **" but no closing "**\n\n" — should NOT be parsed as author.
        ch.Content = "Author: **alice has no closing markers and is just text";

        var yaml = ChallengeYamlSerializer.Serialize(ch, []);

        Assert.DoesNotContain("author:", yaml);
        Assert.Contains("Author: **alice", yaml);
    }

    [Fact]
    public void Serialize_EmptyDefaults_OmitsNoiseFields()
    {
        var ch = StaticAttachment();
        // Leave Hints/FlagTemplate null; MinScoreRate default 0.25;
        // Difficulty default 5; SubmissionLimit default 0; etc.

        var yaml = ChallengeYamlSerializer.Serialize(ch, []);

        Assert.DoesNotContain("hints:", yaml);
        Assert.DoesNotContain("flagTemplate:", yaml);
        Assert.DoesNotContain("minScoreRate:", yaml);
        Assert.DoesNotContain("difficulty:", yaml);
        Assert.DoesNotContain("submissionLimit:", yaml);
        Assert.DoesNotContain("disableBloodBonus:", yaml);
    }

    [Fact]
    public void Serialize_NonDefaultScoringFields_EmittedExplicitly()
    {
        var ch = StaticAttachment();
        ch.MinScoreRate = 0.5;
        ch.Difficulty = 7;
        ch.SubmissionLimit = 10;
        ch.DisableBloodBonus = true;
        ch.Hints = new List<string> { "hint one", "hint two" };

        var yaml = ChallengeYamlSerializer.Serialize(ch, []);

        Assert.Contains("minScoreRate: 0.5", yaml);
        Assert.Contains("difficulty: 7", yaml);
        Assert.Contains("submissionLimit: 10", yaml);
        Assert.Contains("disableBloodBonus: true", yaml);
        Assert.Contains("hints:", yaml);
        Assert.Contains("hint one", yaml);
        Assert.Contains("hint two", yaml);
    }

    [Fact]
    public void Serialize_NonDefaultNetworkMode_EmittedInContainer()
    {
        var ch = StaticAttachment();
        ch.Type = ChallengeType.StaticContainer;
        ch.ContainerImage = "img:1";
        ch.NetworkMode = NetworkMode.Isolated;

        var yaml = ChallengeYamlSerializer.Serialize(ch, ["flag{x}"]);

        Assert.Contains("networkMode: Isolated", yaml);
    }

    [Fact]
    public void Serialize_DefaultNetworkMode_OmittedFromContainer()
    {
        var ch = StaticAttachment();
        ch.Type = ChallengeType.StaticContainer;
        ch.ContainerImage = "img:1";
        // NetworkMode default = Open

        var yaml = ChallengeYamlSerializer.Serialize(ch, ["flag{x}"]);

        Assert.DoesNotContain("networkMode:", yaml);
    }
}
