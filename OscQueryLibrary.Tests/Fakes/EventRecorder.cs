using System.Threading.Channels;
using OpenShock.MinimalEvents;

namespace OscQueryLibrary.Tests.Fakes;

/// <summary>
/// Records every invocation of an event so tests can await them in order.
/// </summary>
internal sealed class EventRecorder<T>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly Channel<T> _events = Channel.CreateUnbounded<T>();

    public static async Task<EventRecorder<T>> Subscribe(IAsyncMinimalEventObservable<T> observable)
    {
        var recorder = new EventRecorder<T>();
        await observable.SubscribeAsync(value =>
        {
            recorder._events.Writer.TryWrite(value);
            return Task.CompletedTask;
        });
        return recorder;
    }

    public Task<T> Next() => _events.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

    public bool HasPending => _events.Reader.TryPeek(out _);
}
