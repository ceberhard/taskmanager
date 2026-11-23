using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace TaskOrganizer.Web.Pages;

public static class RequestExtensions
{
    /// <summary>The path and query of the page being shown: where a shared action sends the browser back to.</summary>
    public static string CurrentUrl(this HttpRequest request) => request.Path + request.QueryString;

    /// <summary>A page's address with the given query values, repeated names allowed (Project=1&amp;Project=3).</summary>
    public static string PageWithQuery(this IUrlHelper url, string page, IEnumerable<KeyValuePair<string, string>> values)
    {
        return url.Page(page) + new QueryBuilder(values).ToQueryString();
    }
}
