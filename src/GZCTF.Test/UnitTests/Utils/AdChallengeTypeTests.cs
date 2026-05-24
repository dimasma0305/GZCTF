using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Utils;

/// <summary>
/// Verifies the ChallengeType.AttackDefense extension predicates behave
/// correctly. The historical 2-bit (static|dynamic × attachment|container)
/// layout doesn't cover A&D; the helpers were rewritten to handle it
/// explicitly. Regression-protect that.
/// </summary>
public class AdChallengeTypeTests
{
    [Fact]
    public void AttackDefense_Is_NotStatic_NotAttachment()
    {
        var type = ChallengeType.AttackDefense;

        Assert.False(type.IsStatic());
        Assert.False(type.IsAttachment());
    }

    [Fact]
    public void AttackDefense_Is_Dynamic_AndContainer()
    {
        // A&D is per-team (dynamic-ish) + container-based — both helpers true.
        var type = ChallengeType.AttackDefense;

        Assert.True(type.IsDynamic());
        Assert.True(type.IsContainer());
    }

    [Fact]
    public void AttackDefense_Is_AttackDefense()
    {
        Assert.True(ChallengeType.AttackDefense.IsAttackDefense());
    }

    [Theory]
    [InlineData(ChallengeType.StaticAttachment)]
    [InlineData(ChallengeType.StaticContainer)]
    [InlineData(ChallengeType.DynamicAttachment)]
    [InlineData(ChallengeType.DynamicContainer)]
    public void NonAD_Types_AreNot_AttackDefense(ChallengeType type)
    {
        Assert.False(type.IsAttackDefense());
    }

    [Fact]
    public void AttackDefense_HasExpectedByteValue()
    {
        // Locks in the wire value — changing this is a breaking change
        // for any persisted Challenge.Type column.
        Assert.Equal(0b100, (byte)ChallengeType.AttackDefense);
    }

    [Theory]
    [InlineData(ChallengeType.StaticAttachment, true, false, true, false)]
    [InlineData(ChallengeType.StaticContainer, true, false, false, true)]
    [InlineData(ChallengeType.DynamicAttachment, false, true, true, false)]
    [InlineData(ChallengeType.DynamicContainer, false, true, false, true)]
    public void Existing_Types_PreserveTheirPredicates(
        ChallengeType type, bool isStatic, bool isDynamic, bool isAttachment, bool isContainer)
    {
        // Regression: the rewrite to add AttackDefense handling must not change
        // any existing-type predicate outcome.
        Assert.Equal(isStatic, type.IsStatic());
        Assert.Equal(isDynamic, type.IsDynamic());
        Assert.Equal(isAttachment, type.IsAttachment());
        Assert.Equal(isContainer, type.IsContainer());
    }
}
