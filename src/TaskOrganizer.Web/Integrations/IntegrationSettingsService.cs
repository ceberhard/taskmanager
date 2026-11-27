using Microsoft.EntityFrameworkCore;
using TaskOrganizer.Web.Data;
using TaskOrganizer.Web.Domain;
using TaskOrganizer.Web.Services;

namespace TaskOrganizer.Web.Integrations;

/// <summary>Saves and reads everything the Jira and Azure DevOps integrations need, and builds their connections.</summary>
public class IntegrationSettingsService
{
    private const int MaxPrefixLength = 20;

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly CredentialStore _credentials;
    private readonly ILogger<IntegrationSettingsService> _logger;

    public IntegrationSettingsService(AppDbContext db, SettingsService settings, CredentialStore credentials, ILogger<IntegrationSettingsService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IntegrationSettingsService>.Instance;
        _db = db;
        _settings = settings;
        _credentials = credentials;
    }

    public bool HasJiraToken => _credentials.Has(CredentialStore.JiraApiToken);

    public bool HasAzureDevOpsToken => _credentials.Has(CredentialStore.AzureDevOpsToken);

    /// <summary>Saves the Jira address and email. A token is saved only when one is typed; an empty box keeps the saved one.</summary>
    public async Task SaveJiraAsync(string? siteUrl, string? email, string? apiToken)
    {
        string cleanSite = RequireWebAddress(siteUrl, "Jira site address");
        string cleanEmail = (email ?? string.Empty).Trim();
        if (cleanEmail.Length == 0 || !cleanEmail.Contains('@'))
        {
            throw new DomainRuleException("Enter the email address your Jira API token belongs to.");
        }

        // Token first: if it cannot be written, the address stays as it was and still matches the saved token.
        SaveTokenIfTyped(CredentialStore.JiraApiToken, apiToken, "Jira");
        AppSettings settings = await _settings.GetAsync();
        settings.JiraSiteUrl = cleanSite;
        settings.JiraEmail = cleanEmail;
        await _db.SaveChangesAsync();
    }

    public async Task SaveAzureDevOpsAsync(string? organizationUrl, string? project, string? personalAccessToken)
    {
        string cleanOrganization = RequireWebAddress(organizationUrl, "Azure DevOps organisation address");
        SaveTokenIfTyped(CredentialStore.AzureDevOpsToken, personalAccessToken, "Azure DevOps");
        AppSettings settings = await _settings.GetAsync();
        settings.AzureDevOpsOrganizationUrl = cleanOrganization;
        settings.AzureDevOpsProject = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
        await _db.SaveChangesAsync();
    }

    public async Task SavePullRequestDefaultsAsync(int? projectId, double defaultHours, TaskPriority defaultPriority)
    {
        if (!HoursRules.IsValidSize(defaultHours))
        {
            throw new DomainRuleException($"The default size is {HoursRules.StepProblem}.");
        }

        if (!Enum.IsDefined(defaultPriority))
        {
            throw new DomainRuleException("Unknown priority.");
        }

        if (projectId is int id && !await _db.Projects.AnyAsync(p => p.Id == id))
        {
            throw new DomainRuleException("Project not found.");
        }

        AppSettings settings = await _settings.GetAsync();
        settings.PullRequestProjectId = projectId;
        settings.PullRequestDefaultHours = defaultHours;
        settings.PullRequestDefaultPriority = defaultPriority;
        await _db.SaveChangesAsync();
    }

    public void RemoveToken(string name) => _credentials.Remove(name);

    public async Task<List<JiraProjectMapping>> ListMappingsAsync()
    {
        return await _db.JiraProjectMappings.Include(m => m.Project).OrderBy(m => m.Prefix).ToListAsync();
    }

    public async Task AddMappingAsync(string? prefix, int projectId)
    {
        string cleanPrefix = (prefix ?? string.Empty).Trim().TrimEnd('-').ToUpperInvariant();
        if (cleanPrefix.Length == 0 || cleanPrefix.Length > MaxPrefixLength || !cleanPrefix.All(char.IsAsciiLetterOrDigit))
        {
            throw new DomainRuleException($"A Jira prefix is the letters before the dash in a ticket key, such as ROC (up to {MaxPrefixLength} characters).");
        }

        if (!await _db.Projects.AnyAsync(p => p.Id == projectId))
        {
            throw new DomainRuleException("Project not found.");
        }

        if (await _db.JiraProjectMappings.AnyAsync(m => m.Prefix == cleanPrefix))
        {
            throw new DomainRuleException($"{cleanPrefix} is already mapped. Remove it first to change it.");
        }

        _db.JiraProjectMappings.Add(new JiraProjectMapping { Prefix = cleanPrefix, ProjectId = projectId });
        await _db.SaveChangesAsync();
    }

    public async Task RemoveMappingAsync(int id)
    {
        JiraProjectMapping mapping = await _db.JiraProjectMappings.FindAsync(id) ?? throw new DomainRuleException("That mapping no longer exists.");
        _db.JiraProjectMappings.Remove(mapping);
        await _db.SaveChangesAsync();
    }

    /// <summary>The project a ticket key's prefix is mapped to, or null for Unfiled.</summary>
    public async Task<int?> ProjectForJiraKeyAsync(string key)
    {
        string prefix = key.Split('-')[0];
        JiraProjectMapping? mapping = await _db.JiraProjectMappings.FirstOrDefaultAsync(m => m.Prefix == prefix);
        return mapping?.ProjectId;
    }

    public async Task<JiraConnection> GetJiraConnectionAsync()
    {
        AppSettings settings = await _settings.GetAsync();
        string? token = _credentials.Read(CredentialStore.JiraApiToken);
        if (string.IsNullOrWhiteSpace(settings.JiraSiteUrl) || string.IsNullOrWhiteSpace(settings.JiraEmail) || token is null)
        {
            throw new ExternalServiceException("Jira is not set up yet. Add the site address, email and API token in Settings.");
        }

        return new JiraConnection(settings.JiraSiteUrl, settings.JiraEmail, token);
    }

    public async Task<AzureDevOpsConnection> GetAzureDevOpsConnectionAsync()
    {
        AppSettings settings = await _settings.GetAsync();
        string? token = _credentials.Read(CredentialStore.AzureDevOpsToken);
        if (string.IsNullOrWhiteSpace(settings.AzureDevOpsOrganizationUrl) || token is null)
        {
            throw new ExternalServiceException("Azure DevOps is not set up yet. Add the organisation address and a personal access token in Settings.");
        }

        return new AzureDevOpsConnection(settings.AzureDevOpsOrganizationUrl, settings.AzureDevOpsProject, token);
    }

    private void SaveTokenIfTyped(string name, string? token, string service)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        try
        {
            _credentials.Save(name, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The {Service} token could not be written to {Folder}: {Reason}", service, _credentials.Folder, ex.Message);
            throw new ExternalServiceException($"The {service} token could not be saved in {_credentials.Folder} ({ex.Message}). Nothing was changed.");
        }
    }

    private static string RequireWebAddress(string? value, string what)
    {
        string clean = (value ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(clean, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new DomainRuleException($"The {what} must be a full https:// address.");
        }

        return clean;
    }
}
