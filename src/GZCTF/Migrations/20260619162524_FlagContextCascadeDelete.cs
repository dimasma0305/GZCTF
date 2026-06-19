using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <inheritdoc />
    public partial class FlagContextCascadeDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FlagContexts_ExerciseChallenges_ExerciseId",
                table: "FlagContexts");

            migrationBuilder.DropForeignKey(
                name: "FK_FlagContexts_GameChallenges_ChallengeId",
                table: "FlagContexts");

            migrationBuilder.AddForeignKey(
                name: "FK_FlagContexts_ExerciseChallenges_ExerciseId",
                table: "FlagContexts",
                column: "ExerciseId",
                principalTable: "ExerciseChallenges",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_FlagContexts_GameChallenges_ChallengeId",
                table: "FlagContexts",
                column: "ChallengeId",
                principalTable: "GameChallenges",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FlagContexts_ExerciseChallenges_ExerciseId",
                table: "FlagContexts");

            migrationBuilder.DropForeignKey(
                name: "FK_FlagContexts_GameChallenges_ChallengeId",
                table: "FlagContexts");

            migrationBuilder.AddForeignKey(
                name: "FK_FlagContexts_ExerciseChallenges_ExerciseId",
                table: "FlagContexts",
                column: "ExerciseId",
                principalTable: "ExerciseChallenges",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_FlagContexts_GameChallenges_ChallengeId",
                table: "FlagContexts",
                column: "ChallengeId",
                principalTable: "GameChallenges",
                principalColumn: "Id");
        }
    }
}
