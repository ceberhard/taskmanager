namespace TaskOrganizer.Web.Domain;

/// <summary>Files Jira tickets whose key starts with Prefix (ROC, CHANGEREQ, ...) under a project. Unmapped prefixes go to Unfiled.</summary>
public class JiraProjectMapping
{
    public int Id { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
}
