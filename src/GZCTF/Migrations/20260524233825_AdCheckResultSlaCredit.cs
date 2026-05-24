using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AdCheckResultSlaCredit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SlaCredit",
                table: "AdCheckResults",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            // Backfill existing rows: Ok (Status 0) → full credit, everything
            // else → 0. We can't reconstruct the recovering (0.5) transitions
            // for historical ticks, but that only mildly under-credits past
            // recoveries — acceptable for in-flight games.
            migrationBuilder.Sql(
                @"UPDATE ""AdCheckResults"" SET ""SlaCredit"" = 1.0 WHERE ""Status"" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SlaCredit",
                table: "AdCheckResults");
        }
    }
}
