using GZCTF.Models.Data;

namespace GZCTF.Utils;

/// <summary>
/// Clamps the event-wide A&amp;D knobs on a <see cref="Game"/> to the same ranges
/// <c>GameInfoModel.Validate</c> enforces on the admin Info page. Applied at the import /
/// repo-discovery write sites, which bypass that validator — without it an imported
/// out-of-range value (e.g. <c>AdTickSeconds=0</c>, which spins the scheduler, or a tick so
/// short the getflag SLA check never fires) would persist AND make the game un-editable via
/// the Info page (Validate would reject the unchanged value on the next save).
/// </summary>
public static class AdConfigBounds
{
    public static void Clamp(Game g)
    {
        if (g.AdTickSeconds is { } t) g.AdTickSeconds = Math.Clamp(t, 15, 3600);
        if (g.AdFlagLifetimeTicks is { } l && l < 1) g.AdFlagLifetimeTicks = 1;
        if (g.AdGetflagWindowFraction is { } f) g.AdGetflagWindowFraction = Math.Clamp(f, 0.01, 0.99);
        if (g.AdMinGracePeriodSeconds is { } gr && gr < 0) g.AdMinGracePeriodSeconds = 0;
        if (g.AdResetCooldownMinutes is { } cd && cd < 0) g.AdResetCooldownMinutes = 0;
        if (g.AdWarmupSeconds is { } w && w < 0) g.AdWarmupSeconds = 0;
    }
}
