using Microsoft.AspNetCore.Components;
using OnVoyage.Web.Studio.Api;

namespace OnVoyage.Web.Studio.Components;

/// <summary>What every creator page needs: a status line, and a way to run an action that reports its own failure.</summary>
public abstract class StudioActionBase : ComponentBase
{
    [Inject] protected NavigationManager Navigation { get; set; } = default!;

    protected string? Message { get; set; }

    protected string? Error { get; set; }

    protected bool Busy { get; private set; }

    /// <summary>Runs a creator's action. A refused action shows the API's own explanation instead of crashing the circuit.</summary>
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
        catch (StudioApiException exception)
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
        catch (StudioApiException exception)
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

/// <summary>The pages of the creator space, which talk to <see cref="IStudioApi"/>.</summary>
public abstract class StudioPageBase : StudioActionBase
{
    [Inject] protected IStudioApi Api { get; set; } = default!;
}
