using System.Collections.Concurrent;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// One thread that plays the UI thread (review of PR #609). It has a
/// <see cref="SynchronizationContext"/>, so an <c>await</c> that starts on it resumes on it, as on
/// Avalonia's dispatcher. A view model that mutates a bound collection from its continuations is
/// then serialized with the test's reads of that collection: the test reads through
/// <see cref="Get{T}"/> on the same thread, instead of racing the view model from the test thread.
/// </summary>
internal sealed class SerialTestUi : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly BlockingCollection<Action> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Exception> _faults = new();
    private readonly Thread _thread;

    public SerialTestUi()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "Test UI thread" };
        _thread.Start();
    }

    /// <summary>Queues <paramref name="action"/>, as <c>Dispatcher.UIThread.Post</c> does.</summary>
    public void Post(Action action) => _queue.Add(action);

    /// <summary>Runs <paramref name="action"/> on the UI thread; completes once it has run.</summary>
    public Task Run(Action action) => Get(() => { action(); return true; });

    /// <summary>Reads a value on the UI thread.</summary>
    public Task<T> Get<T>(Func<T> read)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { done.SetResult(read()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task.WaitAsync(Timeout);
    }

    /// <summary>Starts <paramref name="operation"/> on the UI thread and waits until it ends.</summary>
    public async Task RunAsync(Func<Task> operation) => await (await Get(operation)).WaitAsync(Timeout);

    /// <summary>Stops the thread and fails the test if a queued item threw.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(Timeout);
        if (!_faults.IsEmpty) { throw new AggregateException("The test UI thread faulted.", _faults); }
    }

    private void Pump()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable(_stop.Token))
            {
                try { work(); }
                catch (Exception ex) { _faults.Enqueue(ex); }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed. A continuation posted after this is dropped with the test.
        }
    }

    private sealed class Context(SerialTestUi ui) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ui.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state) =>
            ui.Run(() => d(state)).GetAwaiter().GetResult();

        public override SynchronizationContext CreateCopy() => this;
    }
}
