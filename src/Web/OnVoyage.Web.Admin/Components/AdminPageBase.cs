using Microsoft.AspNetCore.Components;
using OnVoyage.Web.Admin.Api;

namespace OnVoyage.Web.Admin.Components;

/// <summary>What every back-office page needs, whichever API it talks to: a status line, and a way to run an action that reports its own failure.</summary>
public abstract class AdminActionBase : ComponentBase
{
    [Inject] protected NavigationManager Navigation { get; set; } = default!;

    protected string? Message { get; set; }

    protected string? Error { get; set; }

    protected bool Busy { get; private set; }

    /// <summary>Runs an editor's action. A refused action shows the API's own explanation instead of crashing the circuit.</summary>
    protected async Task RunAsync(Func<Task> action, string? done = null)
    {
        Busy = true;
        Message = null;
        Error = null;
        try
        {
            await action();
            Message = done;
        }
        catch (AdminApiException exception)
        {
            Error = exception.Title;
        }
        catch (HttpRequestException)
        {
            Error = "Impossible de joindre le serveur.";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Same, for the initial load of a page.</summary>
    protected async Task<T?> LoadAsync<T>(Func<Task<T>> load)
    {
        try
        {
            return await load();
        }
        catch (AdminApiException exception)
        {
            Error = exception.Title;
        }
        catch (HttpRequestException)
        {
            Error = "Impossible de joindre le serveur.";
        }

        return default;
    }
}

/// <summary>The pages of the editorial back-office (places, stories, configuration…), which talk to <see cref="IAdminApi"/>.</summary>
public abstract class AdminPageBase : AdminActionBase
{
    [Inject] protected IAdminApi Api { get; set; } = default!;
}
