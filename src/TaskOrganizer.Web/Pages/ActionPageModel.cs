using Microsoft.AspNetCore.Mvc.RazorPages;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Pages;

/// <summary>
/// Base for pages whose handlers run one service call and then show the outcome. It holds the
/// one copy of "refuse unreadable input, run, turn a broken rule into a message".
/// </summary>
public abstract class ActionPageModel : PageModel
{
    private const string ErrorKey = "Error";
    private const string FlashKey = "Flash";

    /// <summary>
    /// Runs the action unless a posted value could not be read. Returns true when it succeeded.
    /// A success message returned by the action is shown in green; a failure reason in red.
    /// </summary>
    protected async Task<bool> TryRunAsync(Func<Task<string?>> action, string? unreadableInputMessage = null)
    {
        string? problem = InputProblem(unreadableInputMessage);
        if (problem is not null)
        {
            ShowError(problem);
            return false;
        }

        try
        {
            string? message = await action();
            if (message is not null)
            {
                TempData[FlashKey] = message;
            }

            return true;
        }
        catch (DomainRuleException ex)
        {
            ShowError(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// A value that could not be read (for example a malformed date) must not be dropped
    /// silently. Returns the message to show, or null when every posted value was readable.
    /// </summary>
    protected string? InputProblem(string? unreadableInputMessage = null)
    {
        if (ModelState.IsValid)
        {
            return null;
        }

        if (unreadableInputMessage is not null)
        {
            return unreadableInputMessage;
        }

        IEnumerable<string> problems = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
        return "Nothing was saved. " + string.Join(" ", problems);
    }

    protected void ShowError(string message) => TempData[ErrorKey] = message;
}
