using GZCTF.Services;
using Xunit;

namespace GZCTF.Test.UnitTests.Services;

/// <summary>
/// Tests for the pure iptables+ipset rule-script builder behind the Docker A&amp;D
/// egress isolation. Two ipsets express the asymmetric KotH containment:
/// <c>gzctf_chal</c> for A&amp;D per-team containers (both src + dst of the pivot
/// drop) and <c>gzctf_chal_koth</c> for shared hills (src-only — A&amp;D foothold
/// → hill is a legit play and stays reachable). Checker / gzctf / WG sidecar
/// (in neither set) remain exempt.
/// </summary>
public class AdEgressIsolationTests
{
    [Fact]
    public void BuildRulesScript_PopulatesBothSets_AndContainsAsymmetricKothPivot()
    {
        var script = AdEgressIsolationService.BuildRulesScript(
            adContainerIps: ["172.0.7.10", "172.0.7.14"],
            kothHillIps: ["172.0.7.20"]);

        // Both ipsets created + populated
        Assert.Contains("ipset create gzctf_chal hash:ip -exist", script);
        Assert.Contains("ipset add gzctf_chal 172.0.7.10 -exist", script);
        Assert.Contains("ipset add gzctf_chal 172.0.7.14 -exist", script);
        Assert.Contains("ipset create gzctf_chal_koth hash:ip -exist", script);
        Assert.Contains("ipset add gzctf_chal_koth 172.0.7.20 -exist", script);

        // chain bootstrap + established pass-through
        Assert.Contains("DOCKER-USER -j GZCTF_AD_ISO", script);
        Assert.Contains("--ctstate ESTABLISHED,RELATED -j RETURN", script);

        // team↔team pivot: both ends are A&D containers
        Assert.Contains("-m set --match-set gzctf_chal src     -m set --match-set gzctf_chal dst -j DROP", script);
        // hill → A&D backflow blocked
        Assert.Contains("-m set --match-set gzctf_chal_koth src -m set --match-set gzctf_chal dst -j DROP", script);
        // ad src → koth dst is INTENTIONALLY ABSENT (legit play: foothold attacks hill)
        Assert.DoesNotContain("-m set --match-set gzctf_chal src     -m set --match-set gzctf_chal_koth dst -j DROP", script);

        // cloud metadata + private ranges, blocked from BOTH sets as source
        Assert.Contains("-m set --match-set gzctf_chal src     -d 169.254.0.0/16 -j DROP", script);
        Assert.Contains("-m set --match-set gzctf_chal_koth src -d 169.254.0.0/16 -j DROP", script);
        Assert.Contains("-m set --match-set gzctf_chal src     -d 10.0.0.0/8 -j DROP", script);
        Assert.Contains("-m set --match-set gzctf_chal_koth src -d 10.0.0.0/8 -j DROP", script);
        Assert.Contains("-m set --match-set gzctf_chal src     -d 172.16.0.0/12 -j DROP", script);
        Assert.Contains("-m set --match-set gzctf_chal src     -d 192.168.0.0/16 -j DROP", script);
    }

    [Fact]
    public void BuildRulesScript_SkipsMalformedIps()
    {
        var script = AdEgressIsolationService.BuildRulesScript(
            adContainerIps: ["172.0.7.10", "not-an-ip", ""],
            kothHillIps: ["172.0.7.20", "also-bad"]);

        Assert.Contains("ipset add gzctf_chal 172.0.7.10 -exist", script);
        Assert.Contains("ipset add gzctf_chal_koth 172.0.7.20 -exist", script);
        Assert.DoesNotContain("not-an-ip", script);
        Assert.DoesNotContain("also-bad", script);
    }

    [Fact]
    public void BuildRulesScript_NoKothHills_StillSafeWithEmptySet()
    {
        // Pure A&D game: hill set is created+populated empty, no koth-src rules fire,
        // and the A&D containment is intact.
        var script = AdEgressIsolationService.BuildRulesScript(
            adContainerIps: ["172.0.7.10"],
            kothHillIps: []);

        Assert.Contains("ipset create gzctf_chal_koth hash:ip -exist", script);
        Assert.Contains("ipset flush gzctf_chal_koth", script);
        Assert.Contains("-m set --match-set gzctf_chal src     -m set --match-set gzctf_chal dst -j DROP", script);
        // koth source rules are present but match nothing — that's fine and idempotent
        Assert.Contains("-m set --match-set gzctf_chal_koth src -d 169.254.0.0/16 -j DROP", script);
    }

    [Fact]
    public void BuildTeardownScript_RemovesChainAndBothSets()
    {
        var script = AdEgressIsolationService.BuildTeardownScript();
        Assert.Contains("-D DOCKER-USER -j GZCTF_AD_ISO", script);
        Assert.Contains("-X GZCTF_AD_ISO", script);
        Assert.Contains("ipset destroy gzctf_chal", script);
        Assert.Contains("ipset destroy gzctf_chal_koth", script);
    }
}
