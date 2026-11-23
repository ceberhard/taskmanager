using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

public class SettingsService
{
    private readonly AppDbContext _db;

    public SettingsService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AppSettings> GetAsync()
    {
        return await _db.Settings.SingleAsync(s => s.Id == AppSettings.SingletonId);
    }

    public async Task UpdateSuggestionSettingsAsync(int focusSharePercent, double assumedUnsizedHours, int dueSoonWorkingDays)
    {
        if (focusSharePercent < 0 || focusSharePercent > 100)
        {
            throw new DomainRuleException("Focus share must be between 0 and 100 percent.");
        }

        if (!HoursRules.IsValidSize(assumedUnsizedHours) || assumedUnsizedHours > 24)
        {
            throw new DomainRuleException($"Assumed size is {HoursRules.StepProblem} and a maximum of 24.");
        }

        if (dueSoonWorkingDays < 0 || dueSoonWorkingDays > 30)
        {
            throw new DomainRuleException("The due-soon window must be between 0 and 30 working days.");
        }

        AppSettings settings = await GetAsync();
        settings.FocusSharePercent = focusSharePercent;
        settings.AssumedUnsizedHours = assumedUnsizedHours;
        settings.DueSoonWorkingDays = dueSoonWorkingDays;
        await _db.SaveChangesAsync();
    }

    public async Task SetOpeningRolloverAsync(double openingRolloverHours)
    {
        AppSettings settings = await GetAsync();
        settings.OpeningRolloverHours = openingRolloverHours;
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(double weeklyGoalHours, double openingRolloverHours, DateOnly? rolloverStartDate, double dailyPlanTargetHours)
    {
        if (weeklyGoalHours <= 0 || weeklyGoalHours > 168)
        {
            throw new DomainRuleException("Weekly goal must be between 0 and 168 hours.");
        }

        if (dailyPlanTargetHours <= 0 || dailyPlanTargetHours > 24)
        {
            throw new DomainRuleException("Daily plan target must be between 0 and 24 hours.");
        }

        AppSettings settings = await GetAsync();
        settings.WeeklyGoalHours = weeklyGoalHours;
        settings.OpeningRolloverHours = openingRolloverHours;
        settings.RolloverStartDate = rolloverStartDate;
        settings.DailyPlanTargetHours = dailyPlanTargetHours;
        await _db.SaveChangesAsync();
    }
}
