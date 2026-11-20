namespace TaskOrganizer.Web.Pages;

public static class RequestExtensions
{
    /// <summary>The path and query of the page being shown: where a shared action sends the browser back to.</summary>
    public static string CurrentUrl(this HttpRequest request) => request.Path + request.QueryString;
}
