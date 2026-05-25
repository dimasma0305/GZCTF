using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AdTeamServiceLaunchedWithEgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LaunchedWithEgress",
                table: "AdTeamServices",
                type: "boolean",
                nullable: true);

            // Backfill from the challenge's current egress: existing containers
            // sit on the network matching the current AdAllowEgress, so seed the
            // launch-egress to that value — drift detection then works for them
            // immediately (no need to wait for a relaunch to record it).
            migrationBuilder.Sql(
                "UPDATE \"AdTeamServices\" ts SET \"LaunchedWithEgress\" = c.\"AdAllowEgress\" " +
                "FROM \"GameChallenges\" c WHERE c.\"Id\" = ts.\"ChallengeId\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LaunchedWithEgress",
                table: "AdTeamServices");
        }
    }
}
