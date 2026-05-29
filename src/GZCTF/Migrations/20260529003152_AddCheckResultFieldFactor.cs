using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckResultFieldFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "FieldFactor",
                table: "AdCheckResults",
                type: "double precision",
                nullable: true);

            // Backfill the frozen factor for existing rows = sqrt(current accepted
            // count per game) — the same factor migration 20260528235212 already
            // baked into SlaCredit, so an override of a historical check replays a
            // value consistent with the stored credit. Correct under no churn
            // (true for the live deploy); pre-existing churn is unrecoverable
            // (AdRound stores no per-round count), same caveat as that migration.
            migrationBuilder.Sql("""
                UPDATE "AdCheckResults" cr
                SET "FieldFactor" = sqrt(GREATEST(tc.cnt, 1))
                FROM "AdTeamServices" ts
                JOIN "Participations" p ON p."Id" = ts."ParticipationId"
                JOIN (SELECT "GameId", COUNT(*) AS cnt FROM "Participations"
                      WHERE "Status" = 1 GROUP BY "GameId") tc ON tc."GameId" = p."GameId"  -- Accepted
                WHERE cr."AdTeamServiceId" = ts."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FieldFactor",
                table: "AdCheckResults");
        }
    }
}
