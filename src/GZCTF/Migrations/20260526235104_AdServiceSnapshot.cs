using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AdServiceSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdServiceSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdTeamServiceId = table.Column<int>(type: "integer", nullable: false),
                    AdRoundId = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdServiceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdServiceSnapshots_AdRounds_AdRoundId",
                        column: x => x.AdRoundId,
                        principalTable: "AdRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdServiceSnapshots_AdTeamServices_AdTeamServiceId",
                        column: x => x.AdTeamServiceId,
                        principalTable: "AdTeamServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdServiceSnapshots_AdRoundId",
                table: "AdServiceSnapshots",
                column: "AdRoundId");

            migrationBuilder.CreateIndex(
                name: "IX_AdServiceSnapshots_AdTeamServiceId_AdRoundId",
                table: "AdServiceSnapshots",
                columns: new[] { "AdTeamServiceId", "AdRoundId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdServiceSnapshots");
        }
    }
}
