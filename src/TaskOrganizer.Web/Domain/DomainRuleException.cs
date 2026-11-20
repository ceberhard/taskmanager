namespace TaskOrganizer.Web.Domain;

/// <summary>A request broke a business rule. The message is safe to show to the user.</summary>
public class DomainRuleException : Exception
{
    public DomainRuleException(string message) : base(message)
    {
    }
}
