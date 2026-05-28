using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <summary>
    /// Data fix-up: KothHoldPoints used to multiply the per-tick credit by
    /// sqrt(activeTeams) (borrowed from SLA scoring). That made every stored
    /// HoldCredit value on KothControlResults a fractional multiple of the
    /// configured KothHoldPointsPerTick (e.g. 1×sqrt(3) ≈ 1.73 on a 3-team
    /// game), which produced confusing per-cell values on the scoreboard
    /// (+6.9 etc. instead of clean integers). Going forward AdScoring.KothHoldPoints
    /// returns the flat per-tick credit, but the historical rows still carry
    /// the scaled values until we divide them back down.
    ///
    /// <para>Renormalize by the same sqrt factor — using the CURRENT count of
    /// accepted participations per game. This assumes the team count was
    /// stable across the rows being fixed (true for the live deploy; teams
    /// don't usually churn mid-game). Penalty was always flat 1.0 regardless
    /// of team count, so it doesn't need touching.</para>
    ///
    /// <para>EF's migrations history table prevents accidental double-application.
    /// Manually re-running the UP SQL would over-divide.</para>
    /// </summary>
    public partial class RescaleKothHoldCredit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "KothControlResults" kcr
                SET "HoldCredit" = kcr."HoldCredit" / sqrt(GREATEST(t.team_count, 1))
                FROM (
                    SELECT "GameId", COUNT(*) AS team_count
                    FROM "Participations"
                    WHERE "Status" = 1  -- ParticipationStatus.Accepted
                    GROUP BY "GameId"
                ) t
                WHERE kcr."GameId" = t."GameId"
                  AND kcr."HoldCredit" > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the rescale — multiply back by the same sqrt factor.
            migrationBuilder.Sql("""
                UPDATE "KothControlResults" kcr
                SET "HoldCredit" = kcr."HoldCredit" * sqrt(GREATEST(t.team_count, 1))
                FROM (
                    SELECT "GameId", COUNT(*) AS team_count
                    FROM "Participations"
                    WHERE "Status" = 1
                    GROUP BY "GameId"
                ) t
                WHERE kcr."GameId" = t."GameId"
                  AND kcr."HoldCredit" > 0;
                """);
        }
    }
}
