using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <summary>
    /// Adds the columns the jump-host sidecar needs to validate SSH
    /// connections without scanning every key: <c>Fingerprint</c> (indexed
    /// SHA256 hash the AuthorizedKeysCommand looks up), <c>PlatformGenerated</c>
    /// (flag that the platform owns the private half), and <c>LastUsedAt</c>
    /// (to surface "key used 3min ago" in the UI). Also relaxes
    /// <c>PrivateKey</c> to nullable for the upload-your-own-pubkey path
    /// where the private half never crosses the wire.
    ///
    /// check-migrations: backfill-not-needed
    /// (table is empty in every existing deploy — feature wasn't wired up
    /// before this migration)
    /// </summary>
    public partial class AdSshKeyFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PrivateKey",
                table: "AdTeamSshKeys",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(8192)",
                oldMaxLength: 8192);

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "AdTeamSshKeys",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "PlatformGenerated",
                table: "AdTeamSshKeys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUsedAt",
                table: "AdTeamSshKeys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdTeamSshKeys_Fingerprint",
                table: "AdTeamSshKeys",
                column: "Fingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdTeamSshKeys_Fingerprint",
                table: "AdTeamSshKeys");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "AdTeamSshKeys");

            migrationBuilder.DropColumn(
                name: "PlatformGenerated",
                table: "AdTeamSshKeys");

            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "AdTeamSshKeys");

            migrationBuilder.AlterColumn<string>(
                name: "PrivateKey",
                table: "AdTeamSshKeys",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(8192)",
                oldMaxLength: 8192,
                oldNullable: true);
        }
    }
}
