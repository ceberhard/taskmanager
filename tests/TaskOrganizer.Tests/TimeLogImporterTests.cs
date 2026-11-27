using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Tests;

public class TimeLogImporterTests
{
    private static readonly DateOnly Monday = new(2026, 11, 16);
    private const int HoursPrecision = 4;

    /// <summary>Excel's stored value for a time of day: the fraction of a day.</summary>
    private static double Fraction(int hour, int minute) => (hour * 60 + minute) / 1440.0;

    /// <summary>A workbook laid out like the real Overall sheet, with invented times.</summary>
    private static MemoryStream BuildWorkbook(Action<IXLWorksheet>? customise = null)
    {
        using XLWorkbook workbook = new();
        IXLWorksheet sheet = workbook.AddWorksheet(TimeLogImporter.SheetName);
        sheet.Cell("A1").Value = "Weekly Work Log";
        sheet.Cell("B2").Value = "Start Time";
        sheet.Cell("C2").Value = "End Time";
        sheet.Cell("A3").Value = "Rollover";
        sheet.Cell("D3").Value = 12.5;

        sheet.Cell("A5").Value = new DateTime(2026, 11, 16);
        sheet.Cell("B5").Value = Fraction(8, 0);
        sheet.Cell("C5").Value = Fraction(12, 0);
        sheet.Cell("B6").Value = Fraction(13, 0);
        sheet.Cell("C6").Value = Fraction(16, 30);
        sheet.Cell("A10").Value = "DAILY TOTAL:";
        sheet.Cell("D10").Value = 7.5;

        // The real sheet derives later dates from the first one by formula.
        sheet.Cell("A12").FormulaA1 = "=(A5+1)";
        sheet.Cell("B12").Value = TimeSpan.FromHours(9);
        sheet.Cell("C12").Value = TimeSpan.FromHours(11.25);
        sheet.Cell("A17").Value = "DAILY TOTAL:";
        sheet.Cell("A18").Value = ":";
        sheet.Cell("A25").Value = 6;

        sheet.Cell("A40").Value = "Weekend / Extra";
        sheet.Cell("A47").Value = "WEEKLY TOTAL:";
        sheet.Cell("D47").Value = 9.75;
        sheet.Cell("B52").Value = "CATCH";
        sheet.Cell("C52").Value = "CATCH";
        customise?.Invoke(sheet);

        MemoryStream stream = new();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Each_completed_span_becomes_an_entry_and_the_daily_totals_match_the_sheet()
    {
        // The import replaces retyping the week, so the hours must come out the same as the spreadsheet shows.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        using MemoryStream workbook = BuildWorkbook();

        TimeImportResult result = await host.TimeImporter.ImportAsync(workbook);

        Assert.Equal(3, result.EntriesCreated);
        Assert.Equal(0, result.EntriesSkipped);
        Assert.Empty(result.Warnings);
        WeekSummary week = await host.TimeLog.GetWeekAsync(Monday);
        Assert.Equal(7.5, week.Days[0].TotalHours, HoursPrecision);
        Assert.Equal(2.25, week.Days[1].TotalHours, HoursPrecision);
        Assert.Equal(9.75, week.WeeklyTotalHours, HoursPrecision);
        TimeEntry tuesday = await host.Db.TimeEntries.SingleAsync(e => e.Date == Monday.AddDays(1));
        Assert.Equal(new TimeOnly(9, 0), tuesday.StartTime);
        Assert.Equal(new TimeOnly(11, 15), tuesday.EndTime);
    }

    [Fact]
    public async Task Times_stored_by_excel_as_long_fractions_land_on_the_minute_that_was_typed()
    {
        // 03:57 is stored as 0.16458333...; truncating instead of rounding would shift entries by a minute.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        using MemoryStream workbook = BuildWorkbook(sheet =>
        {
            sheet.Cell("B5").Value = 0.16458333333333333;
            sheet.Cell("C5").Value = 0.46458333333333335;
        });

        await host.TimeImporter.ImportAsync(workbook);

        TimeEntry first = await host.Db.TimeEntries.Where(e => e.Date == Monday).OrderBy(e => e.StartTime).FirstAsync();
        Assert.Equal(new TimeOnly(3, 57), first.StartTime);
        Assert.Equal(new TimeOnly(11, 9), first.EndTime);
    }

    [Fact]
    public async Task Running_the_import_twice_adds_nothing()
    {
        // It will be re-run by accident; duplicated entries would double the week's hours.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        using MemoryStream first = BuildWorkbook();
        await host.TimeImporter.ImportAsync(first);

        using MemoryStream second = BuildWorkbook();
        TimeImportResult result = await host.TimeImporter.ImportAsync(second);

        Assert.Equal(0, result.EntriesCreated);
        Assert.Equal(3, result.EntriesSkipped);
        Assert.Empty(result.Warnings);
        Assert.Equal(3, await host.Db.TimeEntries.CountAsync());
    }

    [Fact]
    public async Task A_start_with_no_end_is_skipped_and_reported_not_imported_as_a_running_entry()
    {
        // The sheet's unfinished row is whatever was running when the file was saved; importing it open would block clocking in.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        using MemoryStream workbook = BuildWorkbook(sheet => sheet.Cell("B13").Value = Fraction(14, 0));

        TimeImportResult result = await host.TimeImporter.ImportAsync(workbook);

        Assert.Equal(3, result.EntriesCreated);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("Row 13", warning);
        Assert.Contains("2026-11-17", warning);
        Assert.Equal(0, await host.Db.TimeEntries.CountAsync(e => e.EndTime == null));
    }

    [Fact]
    public async Task Spans_in_the_undated_weekend_block_are_skipped_and_reported()
    {
        // The block has no date, and filing the hours under a guessed day would put them in the wrong week.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        using MemoryStream workbook = BuildWorkbook(sheet =>
        {
            sheet.Cell("B40").Value = Fraction(10, 0);
            sheet.Cell("C40").Value = Fraction(11, 0);
        });

        TimeImportResult result = await host.TimeImporter.ImportAsync(workbook);

        Assert.Equal(3, result.EntriesCreated);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("Row 40", warning);
        Assert.Contains("weekend", warning);
    }

    [Fact]
    public async Task A_span_that_overlaps_an_entry_already_in_the_app_is_imported_as_its_own_entry()
    {
        // Overlapping time is allowed, so the sheet's spans all come in; only the same start on the same day counts as already imported.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        await host.TimeLog.AddEntryAsync(Monday, new TimeOnly(7, 30), new TimeOnly(9, 0));
        using MemoryStream workbook = BuildWorkbook();

        TimeImportResult result = await host.TimeImporter.ImportAsync(workbook);

        Assert.Equal(3, result.EntriesCreated);
        Assert.Empty(result.Warnings);
        Assert.Equal(4, await host.Db.TimeEntries.CountAsync());
        Assert.All(await host.Db.TimeEntries.ToListAsync(), e => Assert.Equal(TimeEntryKind.Work, e.Kind));
    }

    [Fact]
    public async Task The_rollover_figure_is_reported_but_the_opening_balance_is_left_alone()
    {
        // Overwriting the balance silently would restate every week's "remaining" without the person knowing why.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        await host.Settings.UpdateAsync(40, 3, null, 9);
        using MemoryStream workbook = BuildWorkbook();

        TimeImportResult result = await host.TimeImporter.ImportAsync(workbook);

        Assert.Equal(12.5, result.RolloverHours);
        AppSettings settings = await host.Settings.GetAsync();
        Assert.Equal(3, settings.OpeningRolloverHours);
    }

    [Fact]
    public async Task A_workbook_without_the_overall_sheet_fails_with_a_clear_message()
    {
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        using XLWorkbook other = new();
        other.AddWorksheet("Something Else");
        using MemoryStream stream = new();
        other.SaveAs(stream);
        stream.Position = 0;

        DomainRuleException error = await Assert.ThrowsAsync<DomainRuleException>(() => host.TimeImporter.ImportAsync(stream));

        Assert.Contains(TimeLogImporter.SheetName, error.Message);
    }
}
