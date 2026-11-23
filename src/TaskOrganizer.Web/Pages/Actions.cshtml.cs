using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Pages;

/// <summary>
/// Post-only endpoint for the actions that are available from more than one page
/// (quick-add, interruption, task buttons, clock). Each handler runs one service call
/// and sends the browser back to where it came from.
/// </summary>
public class ActionsModel : ActionPageModel
{
    private readonly TaskService _tasks;
    private readonly TimeLogService _timeLog;

    public ActionsModel(TaskService tasks, TimeLogService timeLog)
    {
        _tasks = tasks;
        _timeLog = timeLog;
    }

    [BindProperty]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet() => RedirectToPage("/Index");

    /// <summary>
    /// Quick-add. Unlike the other actions, a rejected submission is reported beside the
    /// quick-add box with the typed values put back, so nothing has to be entered twice.
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync(string? title, int? projectId, TaskPriority priority, DateOnly? dueDate, double? estimateHours, bool planForToday)
    {
        string? problem = InputProblem();
        if (problem is null)
        {
            try
            {
                TaskDetails details = new(title, projectId, priority == 0 ? TaskPriority.Medium : priority, dueDate, estimateHours);
                WorkTask task = await _tasks.AddAsync(details, TaskSource.Manual, planForToday);
                TempData["Flash"] = $"Added: {task.Title}";
            }
            catch (DomainRuleException ex)
            {
                problem = ex.Message;
            }
        }

        if (problem is not null)
        {
            QuickAddDraft.Save(TempData, problem, Request.Form);
        }

        return RedirectBack();
    }

    public Task<IActionResult> OnPostInterruptAsync()
    {
        return RunAsync(async () =>
        {
            WorkTask task = await _tasks.AddInterruptionAsync();
            return $"{task.Title} logged and added to Today. Rename it when you have a moment.";
        });
    }

    public Task<IActionResult> OnPostEditAsync(int id, string? title, int? projectId, TaskPriority priority, DateOnly? dueDate, double? estimateHours)
    {
        return RunAsync(async () =>
        {
            await _tasks.UpdateDetailsAsync(id, new TaskDetails(title, projectId, priority, dueDate, estimateHours));
            return null;
        });
    }

    public Task<IActionResult> OnPostCompleteAsync(int id) => RunAsync(() => _tasks.CompleteAsync(id));

    public Task<IActionResult> OnPostDropAsync(int id) => RunAsync(() => _tasks.DropAsync(id));

    public Task<IActionResult> OnPostStartAsync(int id) => RunAsync(() => _tasks.StartAsync(id));

    public Task<IActionResult> OnPostPauseAsync(int id) => RunAsync(() => _tasks.PauseAsync(id));

    public Task<IActionResult> OnPostReopenAsync(int id) => RunAsync(() => _tasks.ReopenAsync(id));

    public Task<IActionResult> OnPostDoneForTodayAsync(int id) => RunAsync(() => _tasks.MarkDoneForTodayAsync(id));

    public Task<IActionResult> OnPostReopenForTodayAsync(int id) => RunAsync(() => _tasks.ReopenForTodayAsync(id));

    public Task<IActionResult> OnPostHoldAsync(int id, string? reason, DateOnly? checkBackOn, bool rememberReason)
    {
        return RunAsync(() => _tasks.HoldAsync(id, reason, checkBackOn, rememberReason));
    }

    public Task<IActionResult> OnPostResumeAsync(int id) => RunAsync(() => _tasks.ResumeAsync(id));

    /// <summary>Deleting cannot be undone, so the form must carry an explicit confirmation.</summary>
    public Task<IActionResult> OnPostDeleteAsync(int id, bool confirmDelete)
    {
        return RunAsync(async () =>
        {
            if (!confirmDelete)
            {
                throw new DomainRuleException("Nothing was deleted. Tick the box to confirm.");
            }

            await _tasks.DeleteAsync(id);
            return "Task deleted.";
        });
    }

    public Task<IActionResult> OnPostPlanTodayAsync(int id) => RunAsync(() => _tasks.PlanForTodayAsync(id));

    public Task<IActionResult> OnPostToBacklogAsync(int id) => RunAsync(() => _tasks.ReturnToBacklogAsync(id));

    public Task<IActionResult> OnPostClockInAsync() => RunAsync(() => _timeLog.ClockInAsync());

    public Task<IActionResult> OnPostClockOutAsync()
    {
        return RunAsync(async () =>
        {
            TimeEntry? closed = await _timeLog.ClockOutAsync();
            return closed is null ? "That entry was under a minute long, so it was discarded." : null;
        });
    }

    public Task<IActionResult> OnPostTimeAddAsync(DateOnly date, TimeOnly start, TimeOnly? end)
    {
        return RunAsync(() => _timeLog.AddEntryAsync(date, start, end));
    }

    public Task<IActionResult> OnPostTimeUpdateAsync(int id, TimeOnly start, TimeOnly? end)
    {
        return RunAsync(() => _timeLog.UpdateEntryAsync(id, start, end));
    }

    public Task<IActionResult> OnPostTimeDeleteAsync(int id) => RunAsync(() => _timeLog.DeleteEntryAsync(id));

    private Task<IActionResult> RunAsync(Func<Task> action)
    {
        return RunAsync(async () =>
        {
            await action();
            return null;
        });
    }

    private async Task<IActionResult> RunAsync(Func<Task<string?>> action)
    {
        await TryRunAsync(action);
        return RedirectBack();
    }

    private IActionResult RedirectBack() => LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
}
