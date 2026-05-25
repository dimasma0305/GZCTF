using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AdScoringPauseAndUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdVpnPeers_AssignedIp",
                table: "AdVpnPeers");

            migrationBuilder.DropIndex(
                name: "IX_AdCheckResults_AdTeamServiceId_AdRoundId",
                table: "AdCheckResults");

            migrationBuilder.AddColumn<bool>(
                name: "AdScoringPaused",
                table: "Games",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // check-migrations: backfill-not-needed
            // AdScoringPaused defaults false (the prior implicit behavior) and
            // GameId is backfilled below before its unique index is built.
            migrationBuilder.AddColumn<int>(
                name: "GameId",
                table: "AdVpnPeers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill the denormalized GameId from each peer's participation
            // BEFORE creating the per-game unique index (rows would otherwise
            // all share GameId=0 and collide on duplicate IPs across games).
            migrationBuilder.Sql(
                "UPDATE \"AdVpnPeers\" v SET \"GameId\" = p.\"GameId\" " +
                "FROM \"Participations\" p WHERE v.\"ParticipationId\" = p.\"Id\";");

            // Collapse any pre-existing duplicate check results to one row per
            // (service, round) — keeping the newest (max Id) — so the new
            // unique index can be created. This is exactly the double-count the
            // index now prevents going forward.
            migrationBuilder.Sql(
                "DELETE FROM \"AdCheckResults\" a USING \"AdCheckResults\" b " +
                "WHERE a.\"AdTeamServiceId\" = b.\"AdTeamServiceId\" " +
                "AND a.\"AdRoundId\" = b.\"AdRoundId\" AND a.\"Id\" < b.\"Id\";");

            migrationBuilder.CreateIndex(
                name: "IX_AdVpnPeers_GameId_AssignedIp",
                table: "AdVpnPeers",
                columns: new[] { "GameId", "AssignedIp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdCheckResults_AdTeamServiceId_AdRoundId",
                table: "AdCheckResults",
                columns: new[] { "AdTeamServiceId", "AdRoundId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdVpnPeers_GameId_AssignedIp",
                table: "AdVpnPeers");

            migrationBuilder.DropIndex(
                name: "IX_AdCheckResults_AdTeamServiceId_AdRoundId",
                table: "AdCheckResults");

            migrationBuilder.DropColumn(
                name: "AdScoringPaused",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "GameId",
                table: "AdVpnPeers");

            migrationBuilder.CreateIndex(
                name: "IX_AdVpnPeers_AssignedIp",
                table: "AdVpnPeers",
                column: "AssignedIp");

            migrationBuilder.CreateIndex(
                name: "IX_AdCheckResults_AdTeamServiceId_AdRoundId",
                table: "AdCheckResults",
                columns: new[] { "AdTeamServiceId", "AdRoundId" });
        }
    }
}
