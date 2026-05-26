using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AdSlaCreditTotal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SlaCreditTotal",
                table: "AdTeamServices",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            // Backfill the running total from existing per-tick SLA credit so the
            // live scoreboard matches the prior (sum-the-rows) value for in-flight
            // games. New rows start at 0 and are incremented going forward.
            migrationBuilder.Sql(
                "UPDATE \"AdTeamServices\" ts SET \"SlaCreditTotal\" = COALESCE(s.total, 0) " +
                "FROM (SELECT \"AdTeamServiceId\", SUM(\"SlaCredit\") AS total " +
                "      FROM \"AdCheckResults\" GROUP BY \"AdTeamServiceId\") s " +
                "WHERE ts.\"Id\" = s.\"AdTeamServiceId\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SlaCreditTotal",
                table: "AdTeamServices");
        }
    }
}
