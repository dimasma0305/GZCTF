using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <summary>
    /// Data fix-up paired with the SLA scoring change: the field-size weight
    /// (<c>sqrt(activeTeams)</c>) is now folded into each per-tick SLA credit
    /// WHEN THE CHECK LANDS (AdScoring.SlaFieldFactor), instead of being applied
    /// to the whole credit sum at render time — which let a mid-game accept/reject
    /// retroactively rescale every team's entire SLA history. Existing rows store
    /// the RAW credit, so multiply them up by the same factor once.
    ///
    /// <para>Uses the CURRENT accepted-team count per game — correct as long as
    /// the team count was stable across the stored rows (true for the live deploy;
    /// teams don't churn mid-game). Under that assumption the displayed score is
    /// IDENTICAL before/after (the old render-time multiply applied the same
    /// sqrt), so this is a no-op for current boards and only changes behavior
    /// under future roster churn (then per-tick freezing kicks in).</para>
    ///
    /// <para>EF's migrations history prevents double-application; manually
    /// re-running the UP SQL would over-scale.</para>
    /// </summary>
    public partial class RescaleSlaCreditFieldSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Per-service running total (read by the live scoreboard).
            migrationBuilder.Sql("""
                UPDATE "AdTeamServices" ts
                SET "SlaCreditTotal" = ts."SlaCreditTotal" * sqrt(GREATEST(tc.cnt, 1))
                FROM "Participations" p,
                     (SELECT "GameId", COUNT(*) AS cnt FROM "Participations"
                      WHERE "Status" = 1 GROUP BY "GameId") tc  -- ParticipationStatus.Accepted
                WHERE ts."ParticipationId" = p."Id"
                  AND tc."GameId" = p."GameId"
                  AND ts."SlaCreditTotal" > 0;
                """);

            // Per-tick rows (summed by the frozen-snapshot + timeline views).
            migrationBuilder.Sql("""
                UPDATE "AdCheckResults" cr
                SET "SlaCredit" = cr."SlaCredit" * sqrt(GREATEST(tc.cnt, 1))
                FROM "AdTeamServices" ts
                JOIN "Participations" p ON p."Id" = ts."ParticipationId"
                JOIN (SELECT "GameId", COUNT(*) AS cnt FROM "Participations"
                      WHERE "Status" = 1 GROUP BY "GameId") tc ON tc."GameId" = p."GameId"
                WHERE cr."AdTeamServiceId" = ts."Id"
                  AND cr."SlaCredit" > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "AdTeamServices" ts
                SET "SlaCreditTotal" = ts."SlaCreditTotal" / sqrt(GREATEST(tc.cnt, 1))
                FROM "Participations" p,
                     (SELECT "GameId", COUNT(*) AS cnt FROM "Participations"
                      WHERE "Status" = 1 GROUP BY "GameId") tc
                WHERE ts."ParticipationId" = p."Id"
                  AND tc."GameId" = p."GameId"
                  AND ts."SlaCreditTotal" > 0;
                """);

            migrationBuilder.Sql("""
                UPDATE "AdCheckResults" cr
                SET "SlaCredit" = cr."SlaCredit" / sqrt(GREATEST(tc.cnt, 1))
                FROM "AdTeamServices" ts
                JOIN "Participations" p ON p."Id" = ts."ParticipationId"
                JOIN (SELECT "GameId", COUNT(*) AS cnt FROM "Participations"
                      WHERE "Status" = 1 GROUP BY "GameId") tc ON tc."GameId" = p."GameId"
                WHERE cr."AdTeamServiceId" = ts."Id"
                  AND cr."SlaCredit" > 0;
                """);
        }
    }
}
