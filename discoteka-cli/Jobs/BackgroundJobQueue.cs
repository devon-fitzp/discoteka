using System.Threading.Channels;

namespace discoteka_cli.Jobs;

public enum BackgroundJobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed record BackgroundJob(Guid Id, string Name, Func<CancellationToken, Task> Work);

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
    public Exception? Error { get; }
}

public interface IBackgroundJobQueue : IAsyncDisposable
{
    int PendingCount { get; }
    ValueTask EnqueueAsync(BackgroundJob job, CancellationToken cancellationToken = default);
    event EventHandler<BackgroundJobStatusChangedEventArgs>? JobStatusChanged;
}

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
