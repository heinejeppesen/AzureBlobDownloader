using System.Collections.Concurrent;

namespace AzureBlobDownloader.App.Tests;

/// <summary>
/// Runs a test on one thread with a synchronization context, like the app's UI thread: every continuation
/// and progress report is executed in order on that thread, never concurrently.
/// </summary>
internal static class UiThread
{
    public static void Run(Func<Task> test)
    {
        var previous = SynchronizationContext.Current;
        var context = new PumpingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = test();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.RunUntilComplete();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class PumpingContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // The test has finished; late progress reports are dropped.
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public void Complete() => _queue.CompleteAdding();

        public void RunUntilComplete()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}
