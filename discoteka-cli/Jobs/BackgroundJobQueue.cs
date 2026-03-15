using System.Threading.Channels;

namespace discoteka_cli.Jobs;

/// <summary>Lifecycle state of a <see cref="BackgroundJob"/> within the queue.</summary>
public enum BackgroundJobState
{
    /// <summary>Accepted into the channel but not yet executing.</summary>
    Queued,
    /// <summary>Currently executing on the worker loop.</summary>
    Running,
    /// <summary>Finished without throwing.</summary>
    Completed,
    /// <summary>Threw an exception other than <see cref="OperationCanceledException"/>.</summary>
    Failed,
    /// <summary>Threw <see cref="OperationCanceledException"/> (shutdown or explicit cancel).</summary>
    Canceled
}

/// <summary>
/// An immutable unit of work submitted to <see cref="IBackgroundJobQueue"/>.
/// <see cref="Work"/> receives the queue's shutdown token; jobs should honour cancellation.
/// </summary>
public sealed record BackgroundJob(Guid Id, string Name, Func<CancellationToken, Task> Work);

/// <summary>
/// Event args emitted by <see cref="IBackgroundJobQueue.JobStatusChanged"/> whenever a job
/// transitions to a new <see cref="BackgroundJobState"/>.
/// </summary>
public sealed class BackgroundJobStatusChangedEventArgs : EventArgs
{
    public BackgroundJobStatusChangedEventArgs(BackgroundJob job, BackgroundJobState state, Exception? error = null)
    {
        Job = job;
        State = state;
        Error = error;
    }

    public BackgroundJob Job { get; }
    public BackgroundJobState State { get; }
    /// <summary>Set when <see cref="State"/> is <see cref="BackgroundJobState.Failed"/>.</summary>
    public Exception? Error { get; }
}

/// <summary>
/// A thread-safe, single-worker, unbounded job queue.
/// Jobs run sequentially in the order they were enqueued.
/// </summary>
public interface IBackgroundJobQueue : IAsyncDisposable
{
    /// <summary>Number of jobs that have been accepted but not yet completed (including the running one).</summary>
    int PendingCount { get; }

    /// <summary>Enqueues a job. Returns immediately; execution is asynchronous.</summary>
    ValueTask EnqueueAsync(BackgroundJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires on the thread pool whenever a job changes state.
    /// Subscribers must marshal to the UI thread if they update UI state.
    /// </summary>
    event EventHandler<BackgroundJobStatusChangedEventArgs>? JobStatusChanged;
}

/// <summary>
/// <see cref="IBackgroundJobQueue"/> implementation backed by
/// <see cref="System.Threading.Channels.Channel{T}"/> with a single reader task.
/// <para>
/// Disposal signals shutdown, drains the channel, and awaits the worker task.
/// Jobs already running will receive a cancellation signal via the shutdown token.
/// </para>
/// </summary>
public sealed class BackgroundJobQueue : IBackgroundJobQueue
{
    private readonly Channel<BackgroundJob> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _pending;

    public BackgroundJobQueue()
    {
        _channel = Channel.CreateUnbounded<BackgroundJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _worker = Task.Run(ProcessAsync);
    }

    public int PendingCount => Math.Max(0, _pending);

    public event EventHandler<BackgroundJobStatusChangedEventArgs>? JobStatusChanged;

    public ValueTask EnqueueAsync(BackgroundJob job, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _pending);
        JobStatusChanged?.Invoke(this, new BackgroundJobStatusChangedEventArgs(job, BackgroundJobState.Queued));
        return _channel.Writer.WriteAsync(job, cancellationToken);
    }

    /// <summary>
    /// The single worker loop. Reads jobs from the channel one at a time and runs them.
    /// Any exception from a job is caught and reported via <see cref="JobStatusChanged"/>
    /// without stopping the worker.
    /// </summary>
    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(_shutdown.Token))
            {
                JobStatusChanged?.Invoke(this, new BackgroundJobStatusChangedEventArgs(job, BackgroundJobState.Running));
                try
                {
                    await job.Work(_shutdown.Token);
                    JobStatusChanged?.Invoke(this, new BackgroundJobStatusChangedEventArgs(job, BackgroundJobState.Completed));
                }
                catch (OperationCanceledException)
                {
                    JobStatusChanged?.Invoke(this, new BackgroundJobStatusChangedEventArgs(job, BackgroundJobState.Canceled));
                }
                catch (Exception ex)
                {
                    JobStatusChanged?.Invoke(this, new BackgroundJobStatusChangedEventArgs(job, BackgroundJobState.Failed, ex));
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown token fired — exit worker loop cleanly.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
        }
        _shutdown.Dispose();
    }
}
