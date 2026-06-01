using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRepoWatchFeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepoWatchSyncs");

            migrationBuilder.DropTable(
                name: "RepoWatches");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepoWatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GameId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GitHubTokenEncrypted = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    LastCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastRunUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextRunUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RepoUrl = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Status = table.Column<byte>(type: "smallint", nullable: false),
                    Subpath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TokenStatus = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepoWatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepoWatches_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepoWatchSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepoWatchId = table.Column<int>(type: "integer", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Imported = table.Column<int>(type: "integer", nullable: false),
                    RanAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    Updated = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepoWatchSyncs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepoWatchSyncs_RepoWatches_RepoWatchId",
                        column: x => x.RepoWatchId,
                        principalTable: "RepoWatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepoWatches_GameId",
                table: "RepoWatches",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_RepoWatches_NextRunUtc_Status",
                table: "RepoWatches",
                columns: new[] { "NextRunUtc", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RepoWatchSyncs_RepoWatchId_RanAtUtc",
                table: "RepoWatchSyncs",
                columns: new[] { "RepoWatchId", "RanAtUtc" });
        }
    }
}
