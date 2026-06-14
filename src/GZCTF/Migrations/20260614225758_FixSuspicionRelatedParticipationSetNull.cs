using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class FixSuspicionRelatedParticipationSetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SuspicionEvents_Participations_RelatedParticipationId",
                table: "SuspicionEvents");

            migrationBuilder.AddForeignKey(
                name: "FK_SuspicionEvents_Participations_RelatedParticipationId",
                table: "SuspicionEvents",
                column: "RelatedParticipationId",
                principalTable: "Participations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SuspicionEvents_Participations_RelatedParticipationId",
                table: "SuspicionEvents");

            migrationBuilder.AddForeignKey(
                name: "FK_SuspicionEvents_Participations_RelatedParticipationId",
                table: "SuspicionEvents",
                column: "RelatedParticipationId",
                principalTable: "Participations",
                principalColumn: "Id");
        }
    }
}
