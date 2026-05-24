using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AdPerUserApiToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdTeamApiTokens_ParticipationId",
                table: "AdTeamApiTokens");

            // Add nullable so backfill UPDATE can fill in real CaptainId
            // values before tightening to NOT NULL. The snapshot-default
            // pattern (all-zeros default) would FK-violate on the AddForeignKey
            // below since no AspNetUsers row has Id = 00000000-…-000000000000.
            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "AdTeamApiTokens",
                type: "uuid",
                nullable: true);

            // check-migrations: backfill-not-needed (UPDATE below handles it)
            // Map every existing per-team token to its team's captain — the
            // captain is who rotated the token previously, so attributing the
            // token to them preserves the current usage pattern. Other team
            // members can rotate their own after this lands.
            migrationBuilder.Sql(@"
                UPDATE ""AdTeamApiTokens"" t
                SET ""UserId"" = tm.""CaptainId""
                FROM ""Participations"" p
                JOIN ""Teams"" tm ON tm.""Id"" = p.""TeamId""
                WHERE t.""ParticipationId"" = p.""Id"";
            ");

            // Defensive: any token whose team has no captain (shouldn't happen
            // — teams require a captain — but better than leaving an orphan
            // that fails the NOT NULL alter below).
            migrationBuilder.Sql(@"
                DELETE FROM ""AdTeamApiTokens"" WHERE ""UserId"" IS NULL;
            ");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "AdTeamApiTokens",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdTeamApiTokens_ParticipationId",
                table: "AdTeamApiTokens",
                column: "ParticipationId");

            migrationBuilder.CreateIndex(
                name: "IX_AdTeamApiTokens_UserId_ParticipationId",
                table: "AdTeamApiTokens",
                columns: new[] { "UserId", "ParticipationId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AdTeamApiTokens_AspNetUsers_UserId",
                table: "AdTeamApiTokens",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdTeamApiTokens_AspNetUsers_UserId",
                table: "AdTeamApiTokens");

            migrationBuilder.DropIndex(
                name: "IX_AdTeamApiTokens_ParticipationId",
                table: "AdTeamApiTokens");

            migrationBuilder.DropIndex(
                name: "IX_AdTeamApiTokens_UserId_ParticipationId",
                table: "AdTeamApiTokens");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "AdTeamApiTokens");

            migrationBuilder.CreateIndex(
                name: "IX_AdTeamApiTokens_ParticipationId",
                table: "AdTeamApiTokens",
                column: "ParticipationId",
                unique: true);
        }
    }
}
