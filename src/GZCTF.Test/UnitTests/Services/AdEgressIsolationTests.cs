using GZCTF.Services;
using Xunit;

namespace GZCTF.Test.UnitTests.Services;

/// <summary>
/// Tests for the pure iptables+ipset rule-script builder behind the Docker A&amp;D
/// egress isolation. The block is keyed on an ipset of challenge-container IPs so the
/// checker / gzctf / WG sidecar (not in the set) stay exempt; only real challenge
/// containers are contained (no team↔team pivot, no cloud metadata / private ranges).
/// </summary>
public class AdEgressIsolationTests
{
    [Fact]
    public void BuildRulesScript_PopulatesSet_AndContainsChallengeEgress()
    {
        var script = AdEgressIsolationService.BuildRulesScript(["172.0.7.10", "172.0.7.14"]);

        // ipset populated with the challenge container IPs
        Assert.Contains("ipset create gzctf_chal hash:ip -exist", script);
        Assert.Contains("ipset add gzctf_chal 172.0.7.10 -exist", script);
        Assert.Contains("ipset add gzctf_chal 172.0.7.14 -exist", script);
        // chain bootstrap + established pass-through
        Assert.Contains("DOCKER-USER -j GZCTF_AD_ISO", script);
        Assert.Contains("--ctstate ESTABLISHED,RELATED -j RETURN", script);
        // team↔team pivot: both ends in the set
        Assert.Contains("-m set --match-set gzctf_chal src -m set --match-set gzctf_chal dst -j DROP", script);
        // cloud metadata + private ranges, from any challenge container
        Assert.Contains("-m set --match-set gzctf_chal src -d 169.254.0.0/16 -j DROP", script);
        Assert.Contains("-m set --match-set gzctf_chal src -d 10.0.0.0/8 -j DROP", script);
        Assert.Contains("-m set --match-set gzctf_chal src -d 172.16.0.0/12 -j DROP", script);
        Assert.Contains("-m set --match-set gzctf_chal src -d 192.168.0.0/16 -j DROP", script);
    }

    [Fact]
    public void BuildRulesScript_SkipsMalformedIps()
    {
        var script = AdEgressIsolationService.BuildRulesScript(["172.0.7.10", "not-an-ip", ""]);
        Assert.Contains("ipset add gzctf_chal 172.0.7.10 -exist", script);
        Assert.DoesNotContain("not-an-ip", script);
    }

    [Fact]
    public void BuildTeardownScript_RemovesChainAndSet()
    {
        var script = AdEgressIsolationService.BuildTeardownScript();
        Assert.Contains("-D DOCKER-USER -j GZCTF_AD_ISO", script);
        Assert.Contains("-X GZCTF_AD_ISO", script);
        Assert.Contains("ipset destroy gzctf_chal", script);
    }
}
