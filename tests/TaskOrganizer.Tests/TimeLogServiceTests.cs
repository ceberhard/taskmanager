using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Tests;

public class TimeLogServiceTests
{
    private static readonly DateOnly Monday = new(2026, 11, 16);
    private const int HoursPrecision = 4;

    private static TimeOnly T(int hour, int minute) => new(hour, minute);

    /// <summary>The first three days of the spreadsheet week this app replaces.</summary>
    private static async Task LogSpreadsheetWeekAsync(TestHost host)
    {
        await host.TimeLog.AddEntryAsync(Monday, T(3, 57), T(11, 9));
        await host.TimeLog.AddEntryAsync(Monday, T(15, 26), T(16, 0));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(1), T(5, 15), T(9, 43));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(1), T(11, 37), T(14, 11));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(1), T(16, 21), T(17, 8));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(2), T(4, 18), T(8, 42));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(2), T(11, 56), T(13, 19));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(2), T(13, 38), T(14, 15));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(2), T(15, 30), T(16, 16));
    }

    [Fact]
    public async Task Daily_and_weekly_totals_match_the_spreadsheet_for_the_same_entries()
    {
        // The app replaces the Overall tab, so the same clock times must give the same hours.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        await LogSpreadsheetWeekAsync(host);

        WeekSummary week = await host.TimeLog.GetWeekAsync(new DateOnly(2026, 11, 19));

        Assert.Equal(Monday, week.WeekStart);
        Assert.Equal(7.7667, week.Days[0].TotalHours, HoursPrecision);
        Assert.Equal(7.8167, week.Days[1].TotalHours, HoursPrecision);
        Assert.Equal(7.1667, week.Days[2].TotalHours, HoursPrecision);
        Assert.Equal(0, week.Days[3].TotalHours);
        Assert.Equal(22.75, week.WeeklyTotalHours, HoursPrecision);
        Assert.Equal(40, week.WeeklyGoalHours);
        Assert.Equal(17.25, week.WeeklyRemainingHours, HoursPrecision);
    }

    [Fact]
    public async Task The_opening_rollover_balance_reduces_what_is_left_to_work_overall()
    {
        // Same figures as the spreadsheet: 31.8467 banked + 22.75 this week against a 40-hour goal.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        await host.Settings.UpdateAsync(40, 31.8467, null, 9);
        await LogSpreadsheetWeekAsync(host);

        WeekSummary week = await host.TimeLog.GetWeekAsync(Monday);

        Assert.Equal(31.8467, week.RolloverInHours, HoursPrecision);
        Assert.Equal(54.5967, week.OverallTotalHours, HoursPrecision);
        Assert.Equal(-14.5967, week.OverallRemainingHours, HoursPrecision);
    }

    [Fact]
    public async Task Hours_over_or_under_the_goal_carry_into_the_following_weeks()
    {
        using TestHost host = new(new DateTime(2026, 11, 30, 6, 0, 0));
        await host.Settings.UpdateAsync(40, 0, null, 9);
        // Week of 16 Nov: 44 hours (+4). Week of 23 Nov: 37 hours (-3).
        for (int day = 0; day < 4; day++)
        {
            await host.TimeLog.AddEntryAsync(Monday.AddDays(day), T(6, 0), T(17, 0));
            await host.TimeLog.AddEntryAsync(Monday.AddDays(7 + day), T(6, 0), T(15, 15));
        }

        WeekSummary secondWeek = await host.TimeLog.GetWeekAsync(Monday.AddDays(7));
        WeekSummary thirdWeek = await host.TimeLog.GetWeekAsync(Monday.AddDays(14));

        Assert.Equal(4, secondWeek.RolloverInHours, HoursPrecision);
        Assert.Equal(1, thirdWeek.RolloverInHours, HoursPrecision);
    }

    [Fact]
    public async Task A_week_with_nothing_logged_does_not_count_as_forty_hours_missed()
    {
        // A week of leave must not sink the balance by a full week's goal.
        using TestHost host = new(new DateTime(2026, 11, 30, 6, 0, 0));
        await host.TimeLog.AddEntryAsync(Monday, T(6, 0), T(16, 0));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(1), T(6, 0), T(16, 0));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(2), T(6, 0), T(16, 0));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(3), T(6, 0), T(16, 0));

        WeekSummary twoWeeksLater = await host.TimeLog.GetWeekAsync(Monday.AddDays(14));

        Assert.Equal(0, twoWeeksLater.RolloverInHours, HoursPrecision);
    }

    [Fact]
    public async Task Weeks_before_the_rollover_start_date_are_ignored()
    {
        using TestHost host = new(new DateTime(2026, 11, 30, 6, 0, 0));
        await host.Settings.UpdateAsync(40, 5, Monday.AddDays(7), 9);
        await host.TimeLog.AddEntryAsync(Monday, T(6, 0), T(7, 0));

        WeekSummary week = await host.TimeLog.GetWeekAsync(Monday.AddDays(7));

        Assert.Equal(5, week.RolloverInHours, HoursPrecision);
    }

    [Fact]
    public async Task Clocking_in_and_out_records_one_entry_with_times_from_the_clock()
    {
        using TestHost host = new(new DateTime(2026, 11, 16, 8, 10, 45));

        await host.TimeLog.ClockInAsync();
        host.Time.SetLocal(new DateTime(2026, 11, 16, 11, 40, 5));
        ClockStatus whileRunning = await host.TimeLog.GetClockStatusAsync();
        TimeEntry? closed = await host.TimeLog.ClockOutAsync();

        Assert.NotNull(closed);
        Assert.NotNull(whileRunning.RunningEntry);
        Assert.Equal(3.5, whileRunning.TodayHours, HoursPrecision);
        Assert.Equal(Monday, closed.Date);
        Assert.Equal(T(8, 10), closed.StartTime);
        Assert.Equal(T(11, 40), closed.EndTime);
        WeekSummary week = await host.TimeLog.GetWeekAsync(Monday);
        Assert.Equal(3.5, week.Days[0].TotalHours, HoursPrecision);
    }

    [Fact]
    public async Task Clocking_in_twice_is_rejected_so_hours_are_never_double_counted()
    {
        using TestHost host = new(new DateTime(2026, 11, 16, 8, 0, 0));
        await host.TimeLog.ClockInAsync();

        await Assert.ThrowsAsync<DomainRuleException>(() => host.TimeLog.ClockInAsync());
    }

    [Fact]
    public async Task An_entry_left_open_overnight_is_not_closed_with_a_guessed_time_and_counts_as_zero()
    {
        // Inventing an end time would put hours in the log that were never worked.
        using TestHost host = new(new DateTime(2026, 11, 16, 15, 0, 0));
        await host.TimeLog.ClockInAsync();
        host.Time.SetLocal(new DateTime(2026, 11, 17, 8, 0, 0));

        await Assert.ThrowsAsync<DomainRuleException>(() => host.TimeLog.ClockOutAsync());
        ClockStatus status = await host.TimeLog.GetClockStatusAsync();
        WeekSummary week = await host.TimeLog.GetWeekAsync(Monday);

        Assert.Null(status.RunningEntry);
        Assert.NotNull(status.StaleOpenEntry);
        Assert.Equal(0, week.Days[0].TotalHours);
    }

    [Fact]
    public async Task Clocking_in_is_rejected_while_an_entry_from_an_earlier_day_is_still_open()
    {
        // The forgotten entry has to be fixed first, or its hours would stay at zero unnoticed.
        using TestHost host = new(new DateTime(2026, 11, 16, 15, 0, 0));
        await host.TimeLog.ClockInAsync();
        host.Time.SetLocal(new DateTime(2026, 11, 17, 8, 0, 0));

        DomainRuleException error = await Assert.ThrowsAsync<DomainRuleException>(() => host.TimeLog.ClockInAsync());

        Assert.Contains("2026-11-16", error.Message);
        using AppDbContext fresh = host.NewContext();
        Assert.Equal(1, await fresh.TimeEntries.CountAsync());
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_open_entry()
    {
        // A double-click sends two requests that can both pass an in-code check; only the database can stop the second.
        using TestHost host = new(new DateTime(2026, 11, 16, 8, 0, 0));
        using AppDbContext bypass = host.NewContext();
        bypass.TimeEntries.Add(new TimeEntry { Date = Monday, StartTime = T(8, 0) });
        await bypass.SaveChangesAsync();

        bypass.TimeEntries.Add(new TimeEntry { Date = Monday.AddDays(1), StartTime = T(9, 0) });

        await Assert.ThrowsAsync<DbUpdateException>(() => bypass.SaveChangesAsync());
    }

    [Fact]
    public async Task Clocking_out_in_the_same_minute_discards_the_entry_instead_of_logging_zero_hours()
    {
        using TestHost host = new(new DateTime(2026, 11, 16, 8, 0, 10));
        await host.TimeLog.ClockInAsync();
        host.Time.SetLocal(new DateTime(2026, 11, 16, 8, 0, 50));

        TimeEntry? closed = await host.TimeLog.ClockOutAsync();

        Assert.Null(closed);
        using AppDbContext fresh = host.NewContext();
        Assert.Equal(0, await fresh.TimeEntries.CountAsync());
    }

    [Theory]
    [InlineData(9, 0, 10, 0, 5)]
    [InlineData(7, 0, 8, 30, 5.5)]
    [InlineData(11, 30, 13, 0, 5.5)]
    [InlineData(7, 0, 13, 0, 10)]
    public async Task Overlapping_entries_on_the_same_day_are_allowed_and_each_counts_in_full(int startHour, int startMinute, int endHour, int endMinute, double expectedDayHours)
    {
        // Chris works odd hours, and on a day off he may log PTO for the day and still log the work he did;
        // both must be recorded and both count toward the totals.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        await host.TimeLog.AddEntryAsync(Monday, T(8, 0), T(12, 0));

        await host.TimeLog.AddEntryAsync(Monday, T(startHour, startMinute), T(endHour, endMinute));

        WeekSummary week = await host.TimeLog.GetWeekAsync(Monday);
        Assert.Equal(2, week.Days[0].Entries.Count);
        Assert.Equal(expectedDayHours, week.Days[0].TotalHours, HoursPrecision);
    }

    [Fact]
    public async Task Entries_may_touch_end_to_start_and_the_same_times_are_fine_on_another_day()
    {
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        await host.TimeLog.AddEntryAsync(Monday, T(8, 0), T(12, 0));

        await host.TimeLog.AddEntryAsync(Monday, T(12, 0), T(13, 0));
        await host.TimeLog.AddEntryAsync(Monday, T(6, 0), T(8, 0));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(1), T(8, 0), T(12, 0));

        WeekSummary week = await host.TimeLog.GetWeekAsync(Monday);
        Assert.Equal(7, week.Days[0].TotalHours, HoursPrecision);
    }

    [Fact]
    public async Task A_finished_entry_can_be_logged_while_the_clock_runs_but_a_second_running_entry_cannot()
    {
        // Overlap is fine; two clocks running at once is still a forgotten clock-in, not a way of working.
        using TestHost host = new(new DateTime(2026, 11, 16, 8, 0, 0));
        await host.TimeLog.ClockInAsync();

        await host.TimeLog.AddEntryAsync(Monday, T(9, 0), T(10, 0));

        await Assert.ThrowsAsync<DomainRuleException>(() => host.TimeLog.AddEntryAsync(Monday, T(11, 0), null));
        Assert.Equal(2, await host.Db.TimeEntries.CountAsync());
    }

    [Fact]
    public async Task A_new_entry_is_work_unless_another_type_is_chosen()
    {
        using TestHost host = new(new DateTime(2026, 11, 16, 8, 0, 0));

        TimeEntry clocked = await host.TimeLog.ClockInAsync();
        host.Time.Advance(TimeSpan.FromHours(1));
        await host.TimeLog.ClockOutAsync();
        TimeEntry typedIn = await host.TimeLog.AddEntryAsync(Monday, T(10, 0), T(11, 0));
        TimeEntry pto = await host.TimeLog.AddEntryAsync(Monday.AddDays(1), T(9, 0), T(17, 0), TimeEntryKind.Pto);

        Assert.Equal(TimeEntryKind.Work, typedIn.Kind);
        Assert.Equal(TimeEntryKind.Pto, pto.Kind);
        Assert.Equal(TimeEntryKind.Work, (await host.NewContext().TimeEntries.SingleAsync(e => e.Id == clocked.Id)).Kind);
    }

    [Fact]
    public async Task Pto_holiday_and_sick_time_count_toward_the_week_and_are_shown_separately()
    {
        // Days off still fill the week against the goal; the breakdown shows how much of it was not work.
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        await host.TimeLog.AddEntryAsync(Monday, T(8, 0), T(16, 0), TimeEntryKind.Pto);
        await host.TimeLog.AddEntryAsync(Monday, T(19, 0), T(21, 0));
        await host.TimeLog.AddEntryAsync(Monday.AddDays(1), T(8, 0), T(16, 0), TimeEntryKind.Holiday);
        await host.TimeLog.AddEntryAsync(Monday.AddDays(2), T(8, 0), T(12, 0), TimeEntryKind.Sick);
        await host.TimeLog.AddEntryAsync(Monday.AddDays(2), T(13, 0), T(16, 0));

        WeekSummary week = await host.TimeLog.GetWeekAsync(Monday);

        Assert.Equal(25, week.WeeklyTotalHours, HoursPrecision);
        Assert.Equal(15, week.WeeklyRemainingHours, HoursPrecision);
        Assert.Equal(5, week.HoursFor(TimeEntryKind.Work), HoursPrecision);
        Assert.Equal(8, week.HoursFor(TimeEntryKind.Pto), HoursPrecision);
        Assert.Equal(8, week.HoursFor(TimeEntryKind.Holiday), HoursPrecision);
        Assert.Equal(4, week.HoursFor(TimeEntryKind.Sick), HoursPrecision);
        Assert.Equal(10, week.Days[0].TotalHours, HoursPrecision);
    }

    [Fact]
    public async Task Correcting_an_entry_can_change_its_type_and_leaves_it_alone_when_none_is_given()
    {
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        TimeEntry entry = await host.TimeLog.AddEntryAsync(Monday, T(8, 0), T(16, 0));

        await host.TimeLog.UpdateEntryAsync(entry.Id, T(8, 0), T(16, 0), TimeEntryKind.Sick);
        await host.TimeLog.UpdateEntryAsync(entry.Id, T(9, 0), T(16, 0));

        TimeEntry saved = await host.NewContext().TimeEntries.SingleAsync();
        Assert.Equal((TimeEntryKind.Sick, T(9, 0)), (saved.Kind, saved.StartTime));
    }

    [Fact]
    public async Task Correcting_an_entry_is_not_blocked_by_its_own_old_times()
    {
        using TestHost host = new(new DateTime(2026, 11, 19, 6, 0, 0));
        TimeEntry entry = await host.TimeLog.AddEntryAsync(Monday, T(8, 0), T(12, 0));

        await host.TimeLog.UpdateEntryAsync(entry.Id, T(8, 30), T(11, 30));

        WeekSummary week = await host.TimeLog.GetWeekAsync(Monday);
        Assert.Equal(3, week.Days[0].TotalHours, HoursPrecision);
    }

    [Fact]
    public async Task An_entry_cannot_end_before_it_starts()
    {
        using TestHost host = new(new DateTime(2026, 11, 16, 8, 0, 0));

        await Assert.ThrowsAsync<DomainRuleException>(() => host.TimeLog.AddEntryAsync(Monday, T(10, 0), T(9, 0)));
    }

    [Theory]
    [InlineData(2026, 11, 16)]
    [InlineData(2026, 11, 19)]
    [InlineData(2026, 11, 22)]
    public void Weeks_run_monday_to_sunday_like_the_spreadsheet(int year, int month, int day)
    {
        Assert.Equal(Monday, TimeLogService.WeekStartOf(new DateOnly(year, month, day)));
    }
}
