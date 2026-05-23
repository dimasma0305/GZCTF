using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AddAttackDefense : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AdSnapshotRetentionDays",
                table: "Games",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdWarmupSeconds",
                table: "Games",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AdAllowEgress",
                table: "GameChallenges",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AdAllowSelfReset",
                table: "GameChallenges",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AdAllowSnapshotDownload",
                table: "GameChallenges",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AdCheckerImage",
                table: "GameChallenges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdFlagLifetimeTicks",
                table: "GameChallenges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AdGetflagWindowFraction",
                table: "GameChallenges",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdMinGracePeriodSeconds",
                table: "GameChallenges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AdPutflagWindowFraction",
                table: "GameChallenges",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdResetCooldownMinutes",
                table: "GameChallenges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdTickSeconds",
                table: "GameChallenges",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AdRounds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GameId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScoredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdRounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdRounds_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdTeamServices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ParticipationId = table.Column<int>(type: "integer", nullable: false),
                    ChallengeId = table.Column<int>(type: "integer", nullable: false),
                    ContainerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastResetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SnapshotBlobKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdTeamServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdTeamServices_Containers_ContainerId",
                        column: x => x.ContainerId,
                        principalTable: "Containers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AdTeamServices_GameChallenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "GameChallenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdTeamServices_Participations_ParticipationId",
                        column: x => x.ParticipationId,
                        principalTable: "Participations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdTeamSshKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipationId = table.Column<int>(type: "integer", nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    PrivateKey = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    Algorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdTeamSshKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdTeamSshKeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdTeamSshKeys_Participations_ParticipationId",
                        column: x => x.ParticipationId,
                        principalTable: "Participations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdVpnPeers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipationId = table.Column<int>(type: "integer", nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PrivateKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AssignedIp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdVpnPeers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdVpnPeers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdVpnPeers_Participations_ParticipationId",
                        column: x => x.ParticipationId,
                        principalTable: "Participations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdCheckResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdTeamServiceId = table.Column<int>(type: "integer", nullable: false),
                    AdRoundId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<byte>(type: "smallint", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SourceIp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdCheckResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdCheckResults_AdRounds_AdRoundId",
                        column: x => x.AdRoundId,
                        principalTable: "AdRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdCheckResults_AdTeamServices_AdTeamServiceId",
                        column: x => x.AdTeamServiceId,
                        principalTable: "AdTeamServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdFlags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdTeamServiceId = table.Column<int>(type: "integer", nullable: false),
                    AdRoundId = table.Column<int>(type: "integer", nullable: false),
                    PlantedAtRound = table.Column<int>(type: "integer", nullable: false),
                    Flag = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PlantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdFlags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdFlags_AdRounds_AdRoundId",
                        column: x => x.AdRoundId,
                        principalTable: "AdRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdFlags_AdTeamServices_AdTeamServiceId",
                        column: x => x.AdTeamServiceId,
                        principalTable: "AdTeamServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdAttacks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdFlagId = table.Column<int>(type: "integer", nullable: false),
                    AttackerParticipationId = table.Column<int>(type: "integer", nullable: false),
                    VictimParticipationId = table.Column<int>(type: "integer", nullable: false),
                    ChallengeId = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAtRound = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Points = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdAttacks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdAttacks_AdFlags_AdFlagId",
                        column: x => x.AdFlagId,
                        principalTable: "AdFlags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdAttacks_GameChallenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "GameChallenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdAttacks_Participations_AttackerParticipationId",
                        column: x => x.AttackerParticipationId,
                        principalTable: "Participations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdAttacks_Participations_VictimParticipationId",
                        column: x => x.VictimParticipationId,
                        principalTable: "Participations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdAttacks_AdFlagId",
                table: "AdAttacks",
                column: "AdFlagId");

            migrationBuilder.CreateIndex(
                name: "IX_AdAttacks_AttackerParticipationId_AdFlagId",
                table: "AdAttacks",
                columns: new[] { "AttackerParticipationId", "AdFlagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdAttacks_ChallengeId",
                table: "AdAttacks",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_AdAttacks_SubmittedAtRound",
                table: "AdAttacks",
                column: "SubmittedAtRound");

            migrationBuilder.CreateIndex(
                name: "IX_AdAttacks_VictimParticipationId",
                table: "AdAttacks",
                column: "VictimParticipationId");

            migrationBuilder.CreateIndex(
                name: "IX_AdCheckResults_AdRoundId",
                table: "AdCheckResults",
                column: "AdRoundId");

            migrationBuilder.CreateIndex(
                name: "IX_AdCheckResults_AdTeamServiceId_AdRoundId",
                table: "AdCheckResults",
                columns: new[] { "AdTeamServiceId", "AdRoundId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdFlags_AdRoundId",
                table: "AdFlags",
                column: "AdRoundId");

            migrationBuilder.CreateIndex(
                name: "IX_AdFlags_AdTeamServiceId_PlantedAtRound",
                table: "AdFlags",
                columns: new[] { "AdTeamServiceId", "PlantedAtRound" });

            migrationBuilder.CreateIndex(
                name: "IX_AdFlags_Flag",
                table: "AdFlags",
                column: "Flag");

            migrationBuilder.CreateIndex(
                name: "IX_AdRounds_GameId_Number",
                table: "AdRounds",
                columns: new[] { "GameId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdTeamServices_ChallengeId",
                table: "AdTeamServices",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_AdTeamServices_ContainerId",
                table: "AdTeamServices",
                column: "ContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_AdTeamServices_ParticipationId_ChallengeId",
                table: "AdTeamServices",
                columns: new[] { "ParticipationId", "ChallengeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdTeamSshKeys_ParticipationId",
                table: "AdTeamSshKeys",
                column: "ParticipationId");

            migrationBuilder.CreateIndex(
                name: "IX_AdTeamSshKeys_UserId_ParticipationId",
                table: "AdTeamSshKeys",
                columns: new[] { "UserId", "ParticipationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdVpnPeers_AssignedIp",
                table: "AdVpnPeers",
                column: "AssignedIp");

            migrationBuilder.CreateIndex(
                name: "IX_AdVpnPeers_ParticipationId",
                table: "AdVpnPeers",
                column: "ParticipationId");

            migrationBuilder.CreateIndex(
                name: "IX_AdVpnPeers_UserId_ParticipationId",
                table: "AdVpnPeers",
                columns: new[] { "UserId", "ParticipationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdAttacks");

            migrationBuilder.DropTable(
                name: "AdCheckResults");

            migrationBuilder.DropTable(
                name: "AdTeamSshKeys");

            migrationBuilder.DropTable(
                name: "AdVpnPeers");

            migrationBuilder.DropTable(
                name: "AdFlags");

            migrationBuilder.DropTable(
                name: "AdRounds");

            migrationBuilder.DropTable(
                name: "AdTeamServices");

            migrationBuilder.DropColumn(
                name: "AdSnapshotRetentionDays",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "AdWarmupSeconds",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "AdAllowEgress",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdAllowSelfReset",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdAllowSnapshotDownload",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdCheckerImage",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdFlagLifetimeTicks",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdGetflagWindowFraction",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdMinGracePeriodSeconds",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdPutflagWindowFraction",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdResetCooldownMinutes",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdTickSeconds",
                table: "GameChallenges");
        }
    }
}
