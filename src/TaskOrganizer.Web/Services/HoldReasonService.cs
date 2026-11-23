using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

/// <summary>The saved reasons offered while typing why a task is on hold.</summary>
public class HoldReasonService
{
    private readonly AppDbContext _db;

    public HoldReasonService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<HoldReasonSuggestion>> ListAsync()
    {
        return await _db.HoldReasonSuggestions.OrderBy(r => r.Text).ToListAsync();
    }

    public async Task AddAsync(string? text)
    {
        string cleanText = (text ?? string.Empty).Trim();
        if (cleanText.Length == 0)
        {
            throw new DomainRuleException("The reason cannot be empty.");
        }

        if (cleanText.Length > AppDbContext.HoldReasonMaxLength)
        {
            throw new DomainRuleException($"The reason must be {AppDbContext.HoldReasonMaxLength} characters or fewer.");
        }

        // The Text column is case-insensitive, so this matches regardless of case.
        if (await _db.HoldReasonSuggestions.AnyAsync(r => r.Text == cleanText))
        {
            throw new DomainRuleException($"'{cleanText}' is already in the list.");
        }

        _db.HoldReasonSuggestions.Add(new HoldReasonSuggestion { Text = cleanText });
        await _db.SaveChangesAsync();
    }

    /// <summary>Removes a suggestion. Tasks already on hold with that reason keep it.</summary>
    public async Task RemoveAsync(int id)
    {
        HoldReasonSuggestion suggestion = await _db.HoldReasonSuggestions.FindAsync(id)
            ?? throw new DomainRuleException("That reason is no longer in the list.");
        _db.HoldReasonSuggestions.Remove(suggestion);
        await _db.SaveChangesAsync();
    }
}
