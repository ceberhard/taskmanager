using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace TaskOrganizer.Web.Pages;

/// <summary>
/// What was typed into quick-add when it was rejected, plus the reason. It rides in TempData
/// across the redirect so the bar can show the error beside the box and put the values back,
/// instead of making the person type them again. Values are kept as the raw text submitted,
/// because a rejected value (a malformed date, say) may not parse.
/// </summary>
public record QuickAddDraft(string Error, string? Title, string? ProjectId, string? Priority, string? DueDate, string? EstimateHours, bool PlanForToday)
{
    private const string TempDataKey = "QuickAddDraft";

    public static void Save(ITempDataDictionary tempData, string error, IFormCollection form)
    {
        QuickAddDraft draft = new(
            error,
            form["title"],
            form["projectId"],
            form["priority"],
            form["dueDate"],
            form["estimateHours"],
            string.Equals(form["planForToday"], "true", StringComparison.OrdinalIgnoreCase));
        tempData[TempDataKey] = JsonSerializer.Serialize(draft);
    }

    public static QuickAddDraft? Load(ITempDataDictionary tempData)
    {
        return tempData[TempDataKey] is string json ? JsonSerializer.Deserialize<QuickAddDraft>(json) : null;
    }
}
