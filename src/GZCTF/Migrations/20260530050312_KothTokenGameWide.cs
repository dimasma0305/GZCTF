using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class KothTokenGameWide : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The token is now GAME-WIDE: one row per (team, refresh window) instead of
            // one per (team, hill, window). Existing rows carry distinct ChallengeId
            // values per (ParticipationId, RoundNumber), so once ChallengeId is dropped
            // they would collide on the new (ParticipationId, RoundNumber) UNIQUE index
            // and the CreateIndex below would fail on any live DB. Purge them — the next
            // refresh-window boundary mints one fresh game-wide token per team, and
            // players re-fetch + re-plant. (Tokens are ephemeral per-window state, not
            // historical records, so nothing of value is lost.)
            migrationBuilder.Sql("DELETE FROM \"KothTokens\";");

            migrationBuilder.DropForeignKey(
                name: "FK_KothTokens_GameChallenges_ChallengeId",
                table: "KothTokens");

            migrationBuilder.DropIndex(
                name: "IX_KothTokens_ChallengeId",
                table: "KothTokens");

            migrationBuilder.DropIndex(
                name: "IX_KothTokens_ParticipationId_ChallengeId_RoundNumber",
                table: "KothTokens");

            migrationBuilder.DropColumn(
                name: "ChallengeId",
                table: "KothTokens");

            migrationBuilder.CreateIndex(
                name: "IX_KothTokens_ParticipationId_RoundNumber",
                table: "KothTokens",
                columns: new[] { "ParticipationId", "RoundNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric purge: re-adding ChallengeId with defaultValue 0 would point every
            // existing row at a non-existent GameChallenge (Id 0), so the FK below would
            // fail. Drop the rows first; the next window re-mints under the old schema.
            migrationBuilder.Sql("DELETE FROM \"KothTokens\";");

            migrationBuilder.DropIndex(
                name: "IX_KothTokens_ParticipationId_RoundNumber",
                table: "KothTokens");

            migrationBuilder.AddColumn<int>(
                name: "ChallengeId",
                table: "KothTokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_KothTokens_ChallengeId",
                table: "KothTokens",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_KothTokens_ParticipationId_ChallengeId_RoundNumber",
                table: "KothTokens",
                columns: new[] { "ParticipationId", "ChallengeId", "RoundNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_KothTokens_GameChallenges_ChallengeId",
                table: "KothTokens",
                column: "ChallengeId",
                principalTable: "GameChallenges",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
