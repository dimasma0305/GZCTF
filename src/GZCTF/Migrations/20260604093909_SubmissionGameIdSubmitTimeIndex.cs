using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class SubmissionGameIdSubmitTimeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Submissions_GameId",
                table: "Submissions");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_GameId_SubmitTimeUtc",
                table: "Submissions",
                columns: new[] { "GameId", "SubmitTimeUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Submissions_GameId_SubmitTimeUtc",
                table: "Submissions");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_GameId",
                table: "Submissions",
                column: "GameId");
        }
    }
}
