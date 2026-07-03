using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GZCTF.Migrations
{
    /// <summary>
    /// pg_trgm GIN indexes for the admin submission search (SubmissionRepository.GetSubmissions),
    /// a 4-column leading-wildcard OR (Team.Name / User.UserName / GameChallenge.Title /
    /// Submissions.Answer) that otherwise sequential-scans — degrading with table size, worst on
    /// the large Submissions table. All indexes are CREATE INDEX CONCURRENTLY (with the migration
    /// transaction suppressed, as CONCURRENTLY forbids running inside one) so building them can't
    /// lock the live tables for writes. IF NOT EXISTS keeps it idempotent / re-runnable.
    /// </summary>
    public partial class AddSubmissionAnswerTrgmIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;", suppressTransaction: true);

            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Submissions_Answer_Trgm\" " +
                "ON \"Submissions\" USING gin (\"Answer\" gin_trgm_ops);",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Teams_Name_Trgm\" " +
                "ON \"Teams\" USING gin (\"Name\" gin_trgm_ops);",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_GameChallenges_Title_Trgm\" " +
                "ON \"GameChallenges\" USING gin (\"Title\" gin_trgm_ops);",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_AspNetUsers_UserName_Trgm\" " +
                "ON \"AspNetUsers\" USING gin (\"UserName\" gin_trgm_ops);",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Leave the pg_trgm extension installed (harmless, may be used elsewhere); only drop
            // the indexes this migration added. DROP INDEX CONCURRENTLY also can't run in a tx.
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Submissions_Answer_Trgm\";",
                suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Teams_Name_Trgm\";",
                suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_GameChallenges_Title_Trgm\";",
                suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_AspNetUsers_UserName_Trgm\";",
                suppressTransaction: true);
        }
    }
}
