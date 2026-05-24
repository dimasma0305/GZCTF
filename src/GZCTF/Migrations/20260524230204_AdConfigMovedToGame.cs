using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <summary>
    /// Tick length, flag lifetime, reset cooldown, and snapshot-download were
    /// per-challenge but are event-wide policy (rounds span the whole game —
    /// the round advancer already collapsed per-challenge ticks to one value).
    /// Move them to Games.
    ///
    /// check-migrations: backfill-not-needed
    /// The three nullable ints land NULL on existing rows; every read site
    /// falls back to the same platform default via `?? 120 / ?? 5`, so NULL is
    /// behaviorally identical. AdAllowSnapshotDownload is NOT NULL DEFAULT true
    /// — Postgres ADD COLUMN populates existing rows with true at alter time,
    /// preserving the prior per-challenge default (which was also true).
    /// </summary>
    public partial class AdConfigMovedToGame : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdAllowSnapshotDownload",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdFlagLifetimeTicks",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdResetCooldownMinutes",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdTickSeconds",
                table: "GameChallenges");

            // defaultValue: true — EF scaffolded `false` (the CLR default) but
            // the entity initializer is `= true`; we want existing games to
            // keep snapshots enabled, matching the old per-challenge default.
            migrationBuilder.AddColumn<bool>(
                name: "AdAllowSnapshotDownload",
                table: "Games",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "AdFlagLifetimeTicks",
                table: "Games",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdResetCooldownMinutes",
                table: "Games",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdTickSeconds",
                table: "Games",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdAllowSnapshotDownload",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "AdFlagLifetimeTicks",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "AdResetCooldownMinutes",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "AdTickSeconds",
                table: "Games");

            migrationBuilder.AddColumn<bool>(
                name: "AdAllowSnapshotDownload",
                table: "GameChallenges",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AdFlagLifetimeTicks",
                table: "GameChallenges",
                type: "integer",
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
        }
    }
}
