using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AddKingOfTheHill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "KothHoldPointsPerTick",
                table: "Games",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KothRefreshTicks",
                table: "Games",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KothControlResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GameId = table.Column<int>(type: "integer", nullable: false),
                    ChallengeId = table.Column<int>(type: "integer", nullable: false),
                    AdRoundId = table.Column<int>(type: "integer", nullable: false),
                    ControllingParticipationId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<byte>(type: "smallint", nullable: false),
                    HoldCredit = table.Column<double>(type: "double precision", nullable: false),
                    Penalty = table.Column<double>(type: "double precision", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KothControlResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KothControlResults_AdRounds_AdRoundId",
                        column: x => x.AdRoundId,
                        principalTable: "AdRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KothControlResults_GameChallenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "GameChallenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KothControlResults_Participations_ControllingParticipationId",
                        column: x => x.ControllingParticipationId,
                        principalTable: "Participations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "KothTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GameId = table.Column<int>(type: "integer", nullable: false),
                    ChallengeId = table.Column<int>(type: "integer", nullable: false),
                    ContainerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastRefreshRound = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KothTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KothTargets_Containers_ContainerId",
                        column: x => x.ContainerId,
                        principalTable: "Containers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_KothTargets_GameChallenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "GameChallenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KothTargets_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KothTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ParticipationId = table.Column<int>(type: "integer", nullable: false),
                    ChallengeId = table.Column<int>(type: "integer", nullable: false),
                    RoundNumber = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KothTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KothTokens_GameChallenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "GameChallenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KothTokens_Participations_ParticipationId",
                        column: x => x.ParticipationId,
                        principalTable: "Participations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KothControlResults_AdRoundId",
                table: "KothControlResults",
                column: "AdRoundId");

            migrationBuilder.CreateIndex(
                name: "IX_KothControlResults_ChallengeId_AdRoundId",
                table: "KothControlResults",
                columns: new[] { "ChallengeId", "AdRoundId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KothControlResults_ControllingParticipationId",
                table: "KothControlResults",
                column: "ControllingParticipationId");

            migrationBuilder.CreateIndex(
                name: "IX_KothTargets_ChallengeId",
                table: "KothTargets",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_KothTargets_ContainerId",
                table: "KothTargets",
                column: "ContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_KothTargets_GameId_ChallengeId",
                table: "KothTargets",
                columns: new[] { "GameId", "ChallengeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KothTokens_ChallengeId",
                table: "KothTokens",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_KothTokens_ParticipationId_ChallengeId_RoundNumber",
                table: "KothTokens",
                columns: new[] { "ParticipationId", "ChallengeId", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KothTokens_Token",
                table: "KothTokens",
                column: "Token");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KothControlResults");

            migrationBuilder.DropTable(
                name: "KothTargets");

            migrationBuilder.DropTable(
                name: "KothTokens");

            migrationBuilder.DropColumn(
                name: "KothHoldPointsPerTick",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "KothRefreshTicks",
                table: "Games");
        }
    }
}
