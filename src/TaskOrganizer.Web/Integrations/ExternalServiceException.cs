using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Integrations;

/// <summary>
/// An outside service could not be used: not set up, unreachable, too slow, or it refused the
/// token. The message says which and is safe to show; it never contains the token.
/// </summary>
public class ExternalServiceException : DomainRuleException
{
    public ExternalServiceException(string message) : base(message)
    {
    }
}
