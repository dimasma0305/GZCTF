using GZCTF.Models.Internal;

namespace GZCTF.Services;

/// Risk band — the headline classification an admin triages by. Derived from
/// WHICH evidence tier fired, not from a raw numeric threshold, so that no
/// volume of low-tier (e.g. IP) signals can ever push a team into a high band.
public enum RiskBand : byte
{
    /// No signals at all.
    Clean = 0,
    /// Only network/identity context fired — environmental, not suspicion.
    Context = 1,
    /// Behavioral heuristics only — low confidence.
    Watch = 2,
    /// Strong automation/scanner evidence — worth investigating.
    Investigate = 3,
    /// Hard cross-team evidence — confirmed-grade.
    Evidenced = 4,
}

/// One suspicion event annotated with its tier and whether it actually
/// contributed to the score (false for context, or for incidents beyond the cap).
public sealed class ScoredSuspicionEvent
{
    public string Type { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public DateTimeOffset Time { get; init; }
    public int ScoreDelta { get; init; }
    public SuspicionTier Tier { get; init; }
    public bool Counted { get; init; }
}

/// The fair-scoring breakdown for one participation.
public sealed class SuspicionBreakdown
{
    public int Hard { get; init; }
    public int Strong { get; init; }
    public int Behavioral { get; init; }
    public int Corroboration { get; init; }
    public int Total { get; init; }
    public RiskBand Band { get; init; }
    public List<ScoredSuspicionEvent> Events { get; init; } = new();

    public string BandKey => Band switch
    {
        RiskBand.Evidenced => "evidenced",
        RiskBand.Investigate => "investigate",
        RiskBand.Watch => "watch",
        RiskBand.Context => "context",
        _ => "clean",
    };
}

/// Pure, read-time aggregation of <see cref="Models.Data.SuspicionEvent"/> rows
/// into a tiered risk breakdown. No DB access, no side effects — safe to unit
/// test and to recompute on every report load.
///
/// Invariant (enforced by the band sort, not by arithmetic): a team with zero
/// Hard evidence can never rank above a team with any Hard evidence. Context
/// (IP/identity) signals contribute exactly 0 on their own and only corroborate
/// existing hard evidence, capped at Hard/2.
public static class SuspicionScoring
{
    /// <param name="events">(Type, Details, Time, ScoreDelta) for one participation.</param>
    /// <param name="weight">
    /// Current weight per rule code (admin DB override → default). Passing the
    /// live weight here makes weight edits retroactive without rewriting rows.
    /// </param>
    public static SuspicionBreakdown Compute(
        IEnumerable<(string Type, string Details, DateTimeOffset Time, int ScoreDelta)> events,
        Func<string, int> weight)
    {
        var annotated = new List<ScoredSuspicionEvent>();
        var tierSubtotal = new Dictionary<SuspicionTier, int>();
        var corroborationUnits = 0;
        var contextSeen = new HashSet<string>();

        foreach (var byType in events.GroupBy(e => e.Type))
        {
            var ruleCode = byType.Key;
            var tier = SuspicionType.GetTier(ruleCode);
            var cap = SuspicionType.GetMaxIncidents(ruleCode);
            var w = weight(ruleCode);

            // Distinct incidents = distinct Details (legacy rows have no IncidentKey
            // yet; Details grouping defuses drift, the cap bounds the rest).
            var ordered = byType.OrderBy(e => e.Time).ToList();
            var seenIncident = new HashSet<string>();
            var countedIncidents = 0;

            foreach (var e in ordered)
            {
                var isNewIncident = seenIncident.Add(e.Details);
                // An event "counts" if it's a new, sub-cap incident of a scoring tier.
                var counted = tier > SuspicionTier.Context
                              && isNewIncident
                              && countedIncidents < cap;
                if (counted) countedIncidents++;

                annotated.Add(new ScoredSuspicionEvent
                {
                    Type = e.Type,
                    Details = e.Details,
                    Time = e.Time,
                    ScoreDelta = e.ScoreDelta,
                    Tier = tier,
                    Counted = counted,
                });
            }

            if (tier != SuspicionTier.Context)
            {
                tierSubtotal.TryGetValue(tier, out var sub);
                tierSubtotal[tier] = sub + w * countedIncidents;
            }
            else if (contextSeen.Add(ruleCode))
            {
                // Each distinct context rule lends its corroboration unit once.
                corroborationUnits += SuspicionType.CorroborationUnit(ruleCode);
            }
        }

        var hard = tierSubtotal.GetValueOrDefault(SuspicionTier.Hard);
        var strong = Math.Min(SuspicionType.TierCeiling[SuspicionTier.Strong],
                              tierSubtotal.GetValueOrDefault(SuspicionTier.Strong));
        var behavioral = Math.Min(SuspicionType.TierCeiling[SuspicionTier.Behavioral],
                                  tierSubtotal.GetValueOrDefault(SuspicionTier.Behavioral));
        // Context only corroborates EXISTING hard evidence, and never more than Hard/2.
        var corroboration = hard > 0 ? Math.Min(hard / 2, corroborationUnits) : 0;

        var total = hard + corroboration + strong + behavioral;

        var band =
            hard > 0 ? RiskBand.Evidenced :
            strong > 0 ? RiskBand.Investigate :
            behavioral > 0 ? RiskBand.Watch :
            contextSeen.Count > 0 ? RiskBand.Context :
            RiskBand.Clean;

        return new SuspicionBreakdown
        {
            Hard = hard,
            Strong = strong,
            Behavioral = behavioral,
            Corroboration = corroboration,
            Total = total,
            Band = band,
            Events = annotated,
        };
    }
}
