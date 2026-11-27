using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

/// <summary>RolloverHours is the sheet's Rollover figure, or null when the cell holds no number.</summary>
public record TimeImportResult(int EntriesCreated, int EntriesSkipped, double? RolloverHours, IReadOnlyList<string> Warnings);

/// <summary>
/// Imports the start/end spans from the "Overall" sheet of the old time log. A date in
/// column A starts a day; the rows from there down hold a start time in column B and an end
/// time in column C until the next date. Every entry goes through the normal time log
/// rules, so an import can never create open-ended entries. Imported time is Work.
/// </summary>
public class TimeLogImporter
{
    public const string SheetName = "Overall";
    private const string RolloverLabel = "Rollover";
    private const string WeekendLabelStart = "Weekend";
    private const int DateColumn = 1;
    private const int StartColumn = 2;
    private const int EndColumn = 3;
    private const int DurationColumn = 4;

    /// <summary>Excel date serials below this are not plausible dates (the sheet has stray small numbers in column A).</summary>
    private const double EarliestDateSerial = 36526; // 2000-01-01
    private const int MinutesPerDay = 1440;

    private readonly AppDbContext _db;
    private readonly TimeLogService _timeLog;

    public TimeLogImporter(AppDbContext db, TimeLogService timeLog)
    {
        _db = db;
        _timeLog = timeLog;
    }

    public async Task<TimeImportResult> ImportAsync(string workbookPath)
    {
        using FileStream stream = ImportWorkbook.OpenRead(workbookPath);
        return await ImportAsync(stream);
    }

    public async Task<TimeImportResult> ImportAsync(Stream workbookStream)
    {
        List<string> warnings = new();
        List<ParsedSpan> spans = ReadSpans(workbookStream, warnings, out double? rolloverHours);

        int created = 0;
        int skipped = 0;
        foreach (ParsedSpan span in spans)
        {
            bool alreadyThere = await _db.TimeEntries.AnyAsync(e => e.Date == span.Date && e.StartTime == span.Start);
            if (alreadyThere)
            {
                skipped++;
                continue;
            }

            try
            {
                await _timeLog.AddEntryAsync(span.Date, span.Start, span.End);
                created++;
            }
            catch (DomainRuleException ex)
            {
                // One span that breaks a rule
                // must not stop the rest of the week from coming in.
                warnings.Add($"Row {span.RowNumber}: {span.Date:yyyy-MM-dd} {span.Start:HH\\:mm} to {span.End:HH\\:mm} was skipped. {ex.Message}");
            }
        }

        return new TimeImportResult(created, skipped, rolloverHours, warnings);
    }

    private sealed record ParsedSpan(int RowNumber, DateOnly Date, TimeOnly Start, TimeOnly End);

    private static List<ParsedSpan> ReadSpans(Stream workbookStream, List<string> warnings, out double? rolloverHours)
    {
        using XLWorkbook workbook = new(workbookStream);
        if (!workbook.TryGetWorksheet(SheetName, out IXLWorksheet? sheet) || sheet is null)
        {
            throw new DomainRuleException($"The workbook has no sheet named '{SheetName}'.");
        }

        rolloverHours = null;
        List<ParsedSpan> spans = new();
        DateOnly? currentDate = null;
        bool inUndatedBlock = false;
        int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;

        for (int rowNumber = 1; rowNumber <= lastRow; rowNumber++)
        {
            XLCellValue label = sheet.Cell(rowNumber, DateColumn).Value;
            if (TryReadDate(label, out DateOnly date))
            {
                currentDate = date;
                inUndatedBlock = false;
            }
            else if (label.IsText)
            {
                string text = label.GetText().Trim();
                if (text.StartsWith(RolloverLabel, StringComparison.OrdinalIgnoreCase))
                {
                    XLCellValue rollover = sheet.Cell(rowNumber, DurationColumn).Value;
                    rolloverHours = rollover.IsNumber ? rollover.GetNumber() : null;
                }
                else if (text.StartsWith(WeekendLabelStart, StringComparison.OrdinalIgnoreCase))
                {
                    currentDate = null;
                    inUndatedBlock = true;
                }
            }

            bool hasStart = TryReadTime(sheet.Cell(rowNumber, StartColumn).Value, out TimeOnly start);
            bool hasEnd = TryReadTime(sheet.Cell(rowNumber, EndColumn).Value, out TimeOnly end);
            if (!hasStart && !hasEnd)
            {
                continue;
            }

            if (currentDate is not DateOnly day)
            {
                string reason = inUndatedBlock ? "it is in the weekend block, which has no date" : "no date appears above it";
                warnings.Add($"Row {rowNumber}: skipped because {reason}. Add it on the Time page.");
                continue;
            }

            if (!hasStart || !hasEnd)
            {
                string missing = hasStart ? "end" : "start";
                warnings.Add($"Row {rowNumber}: {day:yyyy-MM-dd} has no {missing} time and was skipped. Add it on the Time page.");
                continue;
            }

            spans.Add(new ParsedSpan(rowNumber, day, start, end));
        }

        return spans;
    }

    private static bool TryReadDate(XLCellValue value, out DateOnly date)
    {
        if (value.IsDateTime)
        {
            date = DateOnly.FromDateTime(value.GetDateTime());
            return true;
        }

        if (value.IsNumber && value.GetNumber() >= EarliestDateSerial)
        {
            date = DateOnly.FromDateTime(DateTime.FromOADate(value.GetNumber()));
            return true;
        }

        date = default;
        return false;
    }

    /// <summary>Excel stores a time of day as a fraction of a day; depending on cell format it arrives as a number, a time span or a date-time.</summary>
    private static bool TryReadTime(XLCellValue value, out TimeOnly time)
    {
        double? fractionOfDay = null;
        if (value.IsTimeSpan)
        {
            fractionOfDay = value.GetTimeSpan().TotalDays;
        }
        else if (value.IsDateTime)
        {
            fractionOfDay = value.GetDateTime().TimeOfDay.TotalDays;
        }
        else if (value.IsNumber)
        {
            fractionOfDay = value.GetNumber();
        }

        if (fractionOfDay is not double fraction || fraction < 0 || fraction >= 1)
        {
            time = default;
            return false;
        }

        int minutes = Math.Min(MinutesPerDay - 1, (int)Math.Round(fraction * MinutesPerDay));
        time = new TimeOnly(minutes / 60, minutes % 60);
        return true;
    }
}
