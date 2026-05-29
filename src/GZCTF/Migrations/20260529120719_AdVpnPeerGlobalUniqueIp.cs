using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class AdVpnPeerGlobalUniqueIp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdVpnPeers_GameId_AssignedIp",
                table: "AdVpnPeers");

            migrationBuilder.CreateIndex(
                name: "IX_AdVpnPeers_AssignedIp",
                table: "AdVpnPeers",
                column: "AssignedIp",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdVpnPeers_AssignedIp",
                table: "AdVpnPeers");

            migrationBuilder.CreateIndex(
                name: "IX_AdVpnPeers_GameId_AssignedIp",
                table: "AdVpnPeers",
                columns: new[] { "GameId", "AssignedIp" },
                unique: true);
        }
    }
}
