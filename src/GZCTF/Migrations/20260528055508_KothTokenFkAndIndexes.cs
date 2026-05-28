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
            migrationBuilder.DropIndex(
                name: "IX_KothTokens_Token",
                table: "KothTokens");

            // Step 1: add AdRoundId as nullable so existing rows can be backfilled.
            // The model declares it NOT NULL; we tighten to NOT NULL below after
            // the backfill + orphan cleanup. Default 0 would point at no real
            // round and break the FK creation otherwise.
            migrationBuilder.AddColumn<int>(
                name: "AdRoundId",
                table: "KothTokens",
                type: "integer",
                nullable: true);

            // Step 2: backfill from (ParticipationId → GameId) + RoundNumber →
            // AdRound.Id. Tokens from games that have since been deleted will
            // miss and get pruned in Step 3.
            migrationBuilder.Sql("""
                UPDATE "KothTokens" k
                SET "AdRoundId" = r."Id"
                FROM "Participations" p, "AdRounds" r
                WHERE k."ParticipationId" = p."Id"
                  AND r."GameId" = p."GameId"
                  AND r."Number" = k."RoundNumber";
                """);

            // Step 3: orphan cleanup — tokens for rounds that no longer exist
            // (or never did) get dropped so the NOT NULL + FK can be applied.
            migrationBuilder.Sql("""DELETE FROM "KothTokens" WHERE "AdRoundId" IS NULL;""");

            // Step 4: duplicate-token cleanup before the unique index. The 144-bit
            // random token makes collisions astronomically unlikely, but if a
            // test rerun or a botched mint produced any, keep only the lowest-Id
            // copy so the new unique index can be built.
            migrationBuilder.Sql("""
                DELETE FROM "KothTokens" a
                USING "KothTokens" b
                WHERE a."Token" = b."Token" AND a."Id" > b."Id";
                """);

            // Step 5: tighten AdRoundId to NOT NULL now that every surviving row has one.
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

            migrationBuilder.CreateIndex(
                name: "IX_KothControlResults_GameId_ChallengeId",
                table: "KothControlResults",
                columns: new[] { "GameId", "ChallengeId" });

            migrationBuilder.AddForeignKey(
                name: "FK_KothTokens_AdRounds_AdRoundId",
                table: "KothTokens",
                column: "AdRoundId",
                principalTable: "AdRounds",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // M4: snapshot of AdAllowEgress at launch — nullable so existing hill
            // rows survive the migration (null reads as "unknown", which the
            // drift check treats as "no drift" → next refresh will populate it).
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
