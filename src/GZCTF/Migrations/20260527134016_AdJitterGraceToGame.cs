using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <summary>
    /// The checker timing knobs (getflag jitter window + min grace period) were
    /// per-challenge but are event-wide policy — they're fractions of the one
    /// shared tick (rounds span the whole game), and the random offset is still
    /// rolled per (team, service, round), so sharing the window size doesn't
    /// weaken anti-fingerprinting. Move them to Games. The putflag jitter window
    /// was never consumed by the scheduler (flags plant at round start) — drop it.
    ///
    /// check-migrations: backfill-not-needed
    /// The two nullable columns land NULL on existing Games; both read sites fall
    /// back to the platform default via `?? 0.5` / `?? 3`, so NULL is behaviorally
    /// identical to the prior per-challenge defaults.
    /// </summary>
    public partial class AdJitterGraceToGame : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdGetflagWindowFraction",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdMinGracePeriodSeconds",
                table: "GameChallenges");

            migrationBuilder.DropColumn(
                name: "AdPutflagWindowFraction",
                table: "GameChallenges");

            migrationBuilder.AddColumn<double>(
                name: "AdGetflagWindowFraction",
                table: "Games",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdMinGracePeriodSeconds",
                table: "Games",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdGetflagWindowFraction",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "AdMinGracePeriodSeconds",
                table: "Games");

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
        }
    }
}
