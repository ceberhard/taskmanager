using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Services;

public class ProjectService
{
    private readonly AppDbContext _db;
    private readonly LocalClock _clock;

    public ProjectService(AppDbContext db, LocalClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<List<Project>> ListAsync()
    {
        return await _db.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync();
    }

    public async Task<Project> AddAsync(string? name)
    {
        string cleanName = await ValidateNameAsync(name, null);
        int nextSortOrder = (await _db.Projects.MaxAsync(p => (int?)p.SortOrder) ?? 0) + 1;
        Project project = new() { Name = cleanName, SortOrder = nextSortOrder, CreatedAtUtc = _clock.UtcNow };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        return project;
    }

    public async Task RenameAsync(int id, string? name)
    {
        Project project = await _db.Projects.FindAsync(id) ?? throw new DomainRuleException("Project not found.");
        project.Name = await ValidateNameAsync(name, id);
        await _db.SaveChangesAsync();
    }

    private async Task<string> ValidateNameAsync(string? name, int? ownId)
    {
        string cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
        {
            throw new DomainRuleException("Project name is required.");
        }

        if (cleanName.Length > 100)
        {
            throw new DomainRuleException("Project name must be 100 characters or fewer.");
        }

        // The Name column uses a case-insensitive collation, so this matches regardless of case.
        bool taken = await _db.Projects.AnyAsync(p => p.Name == cleanName && p.Id != ownId);
        if (taken)
        {
            throw new DomainRuleException($"A project named '{cleanName}' already exists.");
        }

        return cleanName;
    }
}
