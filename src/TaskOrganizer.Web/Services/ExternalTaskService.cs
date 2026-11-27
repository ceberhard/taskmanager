using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

/// <summary>
/// An item from outside the app that should become a task. Key identifies the item for good
/// ("jira:ROC-123", "ado-pr:26512", "slack:C123/1700000000.123"); Url links back to it.
/// </summary>
public record ExternalItem(string Key, string Title, string Url, TaskSource Source, int? ProjectId = null, DateOnly? DueDate = null,
    TaskPriority Priority = TaskPriority.Medium, double? EstimateHours = null);

/// <summary>Created is false when the item was already a task; Task is then the existing one.</summary>
public record ExternalAddResult(WorkTask Task, bool Created);

/// <summary>The one rule for turning outside items (Jira, pull requests, Slack) into tasks.</summary>
public class ExternalTaskService
{
    public const int MaxKeyLength = 200;

    private readonly AppDbContext _db;
    private readonly TaskService _tasks;

    public ExternalTaskService(AppDbContext db, TaskService tasks)
    {
        _db = db;
        _tasks = tasks;
    }

    /// <summary>
    /// Creates the task, or returns the existing one if this item was added before, whatever has
    /// happened to it since. A task that was deleted leaves no trace, so its item can be added again.
    /// </summary>
    public async Task<ExternalAddResult> AddOrGetAsync(ExternalItem item)
    {
        string key = (item.Key ?? string.Empty).Trim();
        if (key.Length == 0 || key.Length > MaxKeyLength)
        {
            throw new DomainRuleException($"An outside item needs a key of 1 to {MaxKeyLength} characters.");
        }

        if (!IsWebLink(item.Url))
        {
            throw new DomainRuleException("An outside item needs a web link starting with http:// or https://.");
        }

        if (item.Url.Length > AppDbContext.ExternalUrlMaxLength)
        {
            throw new DomainRuleException("The link is too long to store.");
        }

        WorkTask? existing = await FindByKeyAsync(key);
        if (existing is not null)
        {
            return new ExternalAddResult(existing, Created: false);
        }

        string title = item.Title.Trim();
        if (title.Length > 1000)
        {
            title = title[..997] + "...";
        }

        TaskDetails details = new(title, item.ProjectId, item.Priority, item.DueDate, item.EstimateHours);
        try
        {
            WorkTask created = await _tasks.AddAsync(details, item.Source, planForToday: false, importKey: key, externalUrl: item.Url);
            return new ExternalAddResult(created, Created: true);
        }
        catch (DbUpdateException)
        {
            // Two requests for the same item at once: the unique key let only one through.
            _db.ChangeTracker.Clear();
            WorkTask winner = await FindByKeyAsync(key) ?? throw new DomainRuleException("The task could not be saved.");
            return new ExternalAddResult(winner, Created: false);
        }
    }

    public static bool IsWebLink(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp);
    }

    private Task<WorkTask?> FindByKeyAsync(string key)
    {
        return _db.Tasks.FirstOrDefaultAsync(t => t.ImportKey == key);
    }
}
