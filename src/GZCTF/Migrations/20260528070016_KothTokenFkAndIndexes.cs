using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class KothTokenFkAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----- KothToken: drop+rebuild Token index as UNIQUE, add AdRoundId FK -----
            migrationBuilder.DropIndex(
                name: "IX_KothTokens_Token",
                table: "KothTokens");

            // Step 1: add AdRoundId as NULLABLE so existing rows can be backfilled.
            // Tightened to NOT NULL below after backfill + orphan cleanup.
            migrationBuilder.AddColumn<int>(
                name: "AdRoundId",
                table: "KothTokens",
                type: "integer",
                nullable: true);

            // Step 2: backfill from (ParticipationId → GameId) + RoundNumber → AdRound.Id.
            // Tokens for games that have since been deleted will miss and get pruned next.
            migrationBuilder.Sql("""
                UPDATE "KothTokens" k
                SET "AdRoundId" = r."Id"
                FROM "Participations" p, "AdRounds" r
                WHERE k."ParticipationId" = p."Id"
                  AND r."GameId" = p."GameId"
                  AND r."Number" = k."RoundNumber";
                """);

            // Step 3: orphan cleanup — tokens that didn't match any round get dropped.
            migrationBuilder.Sql("""DELETE FROM "KothTokens" WHERE "AdRoundId" IS NULL;""");

            // Step 4: duplicate-token cleanup before the unique index. 144-bit random
            // tokens collide with astronomical unlikelihood; this is defensive against
            // test reruns / botched mints.
            migrationBuilder.Sql("""
                DELETE FROM "KothTokens" a
                USING "KothTokens" b
                WHERE a."Token" = b."Token" AND a."Id" > b."Id";
                """);

            // Step 5: tighten AdRoundId to NOT NULL now that every row has one.
            migrationBuilder.AlterColumn<int>(
                name: "AdRoundId",
                table: "KothTokens",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_KothTokens_AdRoundId",
                table: "KothTokens",
                column: "AdRoundId");

            migrationBuilder.CreateIndex(
                name: "IX_KothTokens_Token",
                table: "KothTokens",
                column: "Token",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_KothTokens_AdRounds_AdRoundId",
                table: "KothTokens",
                column: "AdRoundId",
                principalTable: "AdRounds",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // ----- KothControlResults: covering composite index for the scoreboard aggregate -----
            migrationBuilder.CreateIndex(
                name: "IX_KothControlResults_GameId_ChallengeId",
                table: "KothControlResults",
                columns: new[] { "GameId", "ChallengeId" });

            // ----- KothTargets: snapshot of AdAllowEgress for live-toggle drift detection -----
            // Nullable so existing rows survive the migration; the drift check treats
            // null as "unknown" so it doesn't force a spurious refresh on first read.
            migrationBuilder.AddColumn<bool>(
                name: "LaunchedWithEgress",
                table: "KothTargets",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KothTokens_AdRounds_AdRoundId",
                table: "KothTokens");

            migrationBuilder.DropIndex(
                name: "IX_KothTokens_AdRoundId",
                table: "KothTokens");

            migrationBuilder.DropIndex(
                name: "IX_KothTokens_Token",
                table: "KothTokens");

            migrationBuilder.DropIndex(
                name: "IX_KothControlResults_GameId_ChallengeId",
                table: "KothControlResults");

            migrationBuilder.DropColumn(
                name: "AdRoundId",
                table: "KothTokens");

            migrationBuilder.DropColumn(
                name: "LaunchedWithEgress",
                table: "KothTargets");

            migrationBuilder.CreateIndex(
                name: "IX_KothTokens_Token",
                table: "KothTokens",
                column: "Token");
        }
    }
}
