using GZCTF.Models.Request.Game;
using GZCTF.Models.Response.Admin;
using Microsoft.Extensions.Localization;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace GZCTF.Utils;

/// <summary>One accepted A&amp;D flag capture, projected for the activity-log export.</summary>
public sealed record AdAttackLogRow(
    DateTimeOffset SubmittedAt, int Round, string Attacker, string Victim, string Challenge, double Points);

/// <summary>One SLA checker verdict, projected for the activity-log export.</summary>
public sealed record AdCheckLogRow(
    DateTimeOffset CheckedAt, int Round, string Team, string Challenge, string Status, double SlaCredit, string? Error);

public class ExcelHelper(IStringLocalizer<Program> localizer)
{
    private const string Empty = "<empty>";
    private const string Split = " / ";
    private const string Ignore = "-";

    private readonly string[] _commonScoreboardHeader =
    [
        localizer[nameof(Resources.Program.Header_Ranking)],
        localizer[nameof(Resources.Program.Header_Team)],
        localizer[nameof(Resources.Program.Header_Captain)],
        localizer[nameof(Resources.Program.Header_Member)],
        localizer[nameof(Resources.Program.Header_RealName)],
        localizer[nameof(Resources.Program.Header_Email)],
        localizer[nameof(Resources.Program.Header_StdNumber)],
        localizer[nameof(Resources.Program.Header_PhoneNumber)],
        localizer[nameof(Resources.Program.Header_SolvedNumber)],
        localizer[nameof(Resources.Program.Header_ScoringTime)],
        localizer[nameof(Resources.Program.Header_TotalScore)]
    ];

    private readonly string[] _commonSubmissionHeader =
    [
        localizer[nameof(Resources.Program.Header_SubmitStatus)],
        localizer[nameof(Resources.Program.Header_SubmitTime)],
        localizer[nameof(Resources.Program.Header_Team)],
        localizer[nameof(Resources.Program.Header_User)],
        localizer[nameof(Resources.Program.Header_Challenge)],
        localizer[nameof(Resources.Program.Header_SubmitContent)],
        localizer[nameof(Resources.Program.Header_Email)]
    ];

    public MemoryStream GetScoreboardExcel(ScoreboardModel scoreboard)
    {
        if (scoreboard.Items.Values.FirstOrDefault()?.TeamInfo is null)
            throw new ArgumentException(localizer[nameof(Resources.Program.Scoreboard_TeamNotLoaded)]);

        var workbook = new XSSFWorkbook();
        var boardSheet = workbook.CreateSheet(localizer[nameof(Resources.Program.Scoreboard_Title)]);
        var headerStyle = GetHeaderStyle(workbook);
        var challIds = WriteBoardHeader(boardSheet, headerStyle, scoreboard);
        WriteBoardContent(boardSheet, scoreboard, challIds);

        var stream = new MemoryStream();
        workbook.Write(stream, true);
        return stream;
    }

    public MemoryStream GetSubmissionExcel(IEnumerable<Submission> submissions)
    {
        var workbook = new XSSFWorkbook();
        var subSheet = workbook.CreateSheet(localizer[nameof(Resources.Program.Scoreboard_AllSubmissions)]);
        var headerStyle = GetHeaderStyle(workbook);
        WriteSubmissionHeader(subSheet, headerStyle);
        WriteSubmissionContent(subSheet, submissions);

        var stream = new MemoryStream();
        workbook.Write(stream, true);
        return stream;
    }

    /// <summary>
    /// A&amp;D / KotH scoreboard export — the fork's flagship modes carry no jeopardy
    /// score and were excluded from <see cref="GetScoreboardExcel"/>, so their standings
    /// were downloadable nowhere. One row per team: the aggregate breakdown
    /// (attack / defense-loss / SLA / KotH / captures) plus one net-per-service column
    /// per challenge, mirroring the on-screen A&amp;D board. Column headers are English —
    /// the A&amp;D subsystem is fork-added and not part of the localized string catalog.
    /// </summary>
    public MemoryStream GetAdScoreboardExcel(AdScoreboardModel board)
    {
        var workbook = new XSSFWorkbook();
        var sheet = workbook.CreateSheet("A&D Scoreboard");
        var headerStyle = GetHeaderStyle(workbook);

        var headers = new List<string>
        {
            localizer[nameof(Resources.Program.Header_Ranking)],
            localizer[nameof(Resources.Program.Header_Team)],
            "Division", "Total", "Attack", "Defense Loss", "SLA", "KotH",
            "Flags Captured", "Times Captured"
        };
        headers.AddRange(board.Challenges.Select(c => c.Title));
        WriteHeaderRow(sheet.CreateRow(0), headerStyle, headers);

        var challOrder = board.Challenges.Select(c => c.ChallengeId).ToList();
        var rowIndex = 1;
        foreach (var team in board.Teams)
        {
            var row = sheet.CreateRow(rowIndex++);
            var col = 0;
            if (team.Rank == 0)
                row.CreateCell(col++).SetCellValue(Ignore); // unranked
            else
                row.CreateCell(col++).SetCellValue(team.Rank);
            row.CreateCell(col++).SetCellValue(team.TeamName);
            row.CreateCell(col++).SetCellValue(team.Division ?? Ignore);
            row.CreateCell(col++).SetCellValue(team.Total);
            row.CreateCell(col++).SetCellValue(team.AttackPoints);
            row.CreateCell(col++).SetCellValue(team.DefenseLoss);
            row.CreateCell(col++).SetCellValue(team.SlaPoints);
            row.CreateCell(col++).SetCellValue(team.KothPoints);
            row.CreateCell(col++).SetCellValue(team.FlagsCaptured);
            row.CreateCell(col++).SetCellValue(team.TimesCaptured);

            var netByChall = team.Services.ToDictionary(s => s.ChallengeId, s => s.Net);
            foreach (var cid in challOrder)
                row.CreateCell(col++).SetCellValue(netByChall.GetValueOrDefault(cid, 0));
        }

        var stream = new MemoryStream();
        workbook.Write(stream, true);
        return stream;
    }

    /// <summary>
    /// A&amp;D / KotH activity log export: an "Attacks" sheet (every accepted flag capture)
    /// and a "Checks" sheet (every SLA checker verdict). This activity lives in the
    /// <c>AdAttack</c> / <c>AdCheckResult</c> tables, not the jeopardy <c>Submissions</c>
    /// set, so it appeared in no export before. Rows are pre-projected by the caller.
    /// </summary>
    public MemoryStream GetAdActivityExcel(IEnumerable<AdAttackLogRow> attacks, IEnumerable<AdCheckLogRow> checks)
    {
        var workbook = new XSSFWorkbook();
        var headerStyle = GetHeaderStyle(workbook);

        var attackSheet = workbook.CreateSheet("Attacks");
        WriteHeaderRow(attackSheet.CreateRow(0), headerStyle,
            ["Time", "Round", "Attacker", "Victim", "Challenge", "Points"]);
        var r = 1;
        foreach (var a in attacks)
        {
            var row = attackSheet.CreateRow(r++);
            row.CreateCell(0).SetCellValue(a.SubmittedAt.ToString("u"));
            row.CreateCell(1).SetCellValue(a.Round);
            row.CreateCell(2).SetCellValue(a.Attacker);
            row.CreateCell(3).SetCellValue(a.Victim);
            row.CreateCell(4).SetCellValue(a.Challenge);
            row.CreateCell(5).SetCellValue(a.Points);
        }

        var checkSheet = workbook.CreateSheet("Checks");
        WriteHeaderRow(checkSheet.CreateRow(0), headerStyle,
            ["Time", "Round", "Team", "Challenge", "Status", "SLA Credit", "Error"]);
        r = 1;
        foreach (var c in checks)
        {
            var row = checkSheet.CreateRow(r++);
            row.CreateCell(0).SetCellValue(c.CheckedAt.ToString("u"));
            row.CreateCell(1).SetCellValue(c.Round);
            row.CreateCell(2).SetCellValue(c.Team);
            row.CreateCell(3).SetCellValue(c.Challenge);
            row.CreateCell(4).SetCellValue(c.Status);
            row.CreateCell(5).SetCellValue(c.SlaCredit);
            row.CreateCell(6).SetCellValue(c.Error ?? string.Empty);
        }

        var stream = new MemoryStream();
        workbook.Write(stream, true);
        return stream;
    }

    private static void WriteHeaderRow(IRow row, ICellStyle style, IReadOnlyList<string> headers)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            var cell = row.CreateCell(i);
            cell.SetCellValue(headers[i]);
            cell.CellStyle = style;
        }
    }

    private static ICellStyle GetHeaderStyle(XSSFWorkbook workbook)
    {
        var style = workbook.CreateCellStyle();
        var boldFontStyle = workbook.CreateFont();

        boldFontStyle.IsBold = true;
        style.SetFont(boldFontStyle);
        style.BorderBottom = BorderStyle.Medium;
        style.VerticalAlignment = VerticalAlignment.Center;
        style.Alignment = HorizontalAlignment.Center;

        return style;
    }

    private void WriteSubmissionHeader(ISheet sheet, ICellStyle style)
    {
        var row = sheet.CreateRow(0);
        var colIndex = 0;

        foreach (var col in _commonSubmissionHeader)
        {
            var cell = row.CreateCell(colIndex++);
            cell.SetCellValue(col);
            cell.CellStyle = style;
        }
    }

    private void WriteSubmissionContent(ISheet sheet, IEnumerable<Submission> submissions)
    {
        var rowIndex = 1;

        foreach (var item in submissions)
        {
            var colIndex = 0;
            var row = sheet.CreateRow(rowIndex);
            row.CreateCell(colIndex++).SetCellValue(item.Status.ToShortString(localizer));
            row.CreateCell(colIndex++).SetCellValue(item.SubmitTimeUtc.ToString("u"));
            row.CreateCell(colIndex++).SetCellValue(item.TeamName);
            row.CreateCell(colIndex++).SetCellValue(item.UserName);
            row.CreateCell(colIndex++).SetCellValue(item.ChallengeName);
            row.CreateCell(colIndex++).SetCellValue(item.Answer);
            row.CreateCell(colIndex).SetCellValue(item.User?.Email ?? string.Empty);

            rowIndex++;
        }
    }

    private int[] WriteBoardHeader(ISheet sheet, ICellStyle style, ScoreboardModel scoreboard)
    {
        var row = sheet.CreateRow(0);
        var colIndex = 0;
        var challIds = new List<int>();
        var withDiv = scoreboard.Divisions.Count > 0;

        foreach (var col in _commonScoreboardHeader)
        {
            var cell = row.CreateCell(colIndex++);
            cell.SetCellValue(col);
            cell.CellStyle = style;

            if (!withDiv || colIndex != 2)
                continue;

            cell = row.CreateCell(colIndex++);
            cell.SetCellValue(localizer[nameof(Resources.Program.Scoreboard_BelongingDivision)]);
            cell.CellStyle = style;
        }

        // Jeopardy scoreboard export — exclude A&D/KotH challenges (they have their
        // own boards and carry no jeopardy score), matching the on-screen board.
        foreach (var type in scoreboard.Challenges)
            foreach (var chall in type.Value.Where(c =>
                         c.Type is not (ChallengeType.AttackDefense or ChallengeType.KingOfTheHill)))
            {
                var cell = row.CreateCell(colIndex++);
                cell.SetCellValue(chall.Title);
                cell.CellStyle = style;
                challIds.Add(chall.Id);
            }

        return challIds.ToArray();
    }

    private static void WriteBoardContent(ISheet sheet, ScoreboardModel scoreboard, int[] challIds)
    {
        var rowIndex = 1;
        var withDiv = scoreboard.Divisions.Count > 0;

        foreach (var item in scoreboard.Items.Values)
        {
            var colIndex = 0;
            var row = sheet.CreateRow(rowIndex);

            // rank starts from 1, 0 means unranked
            if (item.Rank == 0)
                row.CreateCell(colIndex++).SetCellValue(Ignore);
            else
                row.CreateCell(colIndex++).SetCellValue(item.Rank);

            row.CreateCell(colIndex++).SetCellValue(item.Name);

            if (withDiv)
            {
                if (item.DivisionId is { } id && scoreboard.Divisions.TryGetValue(id, out var division))
                    row.CreateCell(colIndex++).SetCellValue(division.Name);
                else
                    row.CreateCell(colIndex++).SetCellValue(Ignore);
            }

            row.CreateCell(colIndex++).SetCellValue(TakeIfNotEmpty(item.TeamInfo?.Captain?.UserName));

            var members = item.Participants ?? [];

            row.CreateCell(colIndex++)
                .SetCellValue(string.Join(Split, members.Select(m => TakeIfNotEmpty(m.UserName))));
            row.CreateCell(colIndex++)
                .SetCellValue(string.Join(Split, members.Select(m => TakeIfNotEmpty(m.RealName))));
            row.CreateCell(colIndex++)
                .SetCellValue(string.Join(Split, members.Select(m => TakeIfNotEmpty(m.Email))));
            row.CreateCell(colIndex++)
                .SetCellValue(string.Join(Split, members.Select(m => TakeIfNotEmpty(m.StdNumber))));
            row.CreateCell(colIndex++)
                .SetCellValue(string.Join(Split, members.Select(m => TakeIfNotEmpty(m.PhoneNumber))));

            row.CreateCell(colIndex++).SetCellValue(item.SolvedCount);
            row.CreateCell(colIndex++).SetCellValue(item.LastSubmissionTime.ToString("u"));
            row.CreateCell(colIndex++).SetCellValue(item.Score);

            foreach (var challId in challIds)
            {
                var chall = item.SolvedChallenges.SingleOrDefault(c => c.Id == challId);
                row.CreateCell(colIndex++).SetCellValue(chall?.Score ?? 0);
            }

            rowIndex++;
        }
    }

    private static string TakeIfNotEmpty(string? str) => string.IsNullOrWhiteSpace(str) ? Empty : str;
}
