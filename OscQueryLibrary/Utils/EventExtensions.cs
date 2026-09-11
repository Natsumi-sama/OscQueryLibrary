using OpenShock.MinimalEvents;
using Serilog;

namespace OscQueryLibrary.Utils;

internal static class EventExtensions
{
    /// <summary>
    /// Invokes all handlers and logs instead of throwing if one fails.
    /// </summary>
    public static async Task InvokeSafeAsync<T>(this AsyncMinimalEvent<T> evt, T arg, ILogger logger, string eventName)
    {
        try
        {
            await evt.InvokeAsyncParallel(arg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Error in {EventName} handler", eventName);
        }
    }
}
