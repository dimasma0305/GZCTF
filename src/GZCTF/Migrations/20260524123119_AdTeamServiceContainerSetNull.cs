using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AdTeamServiceContainerSetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdTeamServices_Containers_ContainerId",
                table: "AdTeamServices");

            migrationBuilder.AddForeignKey(
                name: "FK_AdTeamServices_Containers_ContainerId",
                table: "AdTeamServices",
                column: "ContainerId",
                principalTable: "Containers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdTeamServices_Containers_ContainerId",
                table: "AdTeamServices");

            migrationBuilder.AddForeignKey(
                name: "FK_AdTeamServices_Containers_ContainerId",
                table: "AdTeamServices",
                column: "ContainerId",
                principalTable: "Containers",
                principalColumn: "Id");
        }
    }
}
