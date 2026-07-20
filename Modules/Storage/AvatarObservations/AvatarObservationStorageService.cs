using System.Threading.Channels;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed class AvatarObservationStorageService : IAsyncDisposable
{
    private readonly AvatarObservationRepository _repository;
    private readonly Channel<AvatarObservationCapture> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _queuedCount;

    public AvatarObservationStorageService(AvatarObservationRepository repository, int capacity = 4)
    {
        _repository = repository;
        _queue = Channel.CreateBounded<AvatarObservationCapture>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    public event EventHandler<AvatarObservationStorageEventArgs>? WriteCompleted;

    public int QueuedCount => Math.Max(0, Volatile.Read(ref _queuedCount));

    public bool TryEnqueue(AvatarObservationCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!_queue.Writer.TryWrite(capture))
        {
            return false;
        }

        Interlocked.Increment(ref _queuedCount);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        var workerCompleted = false;
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(5));
            workerCompleted = true;
        }
        catch (TimeoutException)
        {
            _shutdown.Cancel();
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(1));
                workerCompleted = true;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                workerCompleted = true;
            }
            catch (TimeoutException)
            {
                // A blocked external drive must not prevent the application from exiting.
            }
        }
        finally
        {
            if (workerCompleted)
            {
                _shutdown.Dispose();
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var capture in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                AvatarObservationWriteResult? result = null;
                Exception? error = null;
                try
                {
                    result = _repository.SaveCapture(capture);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    Interlocked.Decrement(ref _queuedCount);
                }

                WriteCompleted?.Invoke(this, new AvatarObservationStorageEventArgs(capture.SubjectId, result, error));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }
}

public sealed class AvatarObservationStorageEventArgs(
    string subjectId,
    AvatarObservationWriteResult? result,
    Exception? error) : EventArgs
{
    public string SubjectId { get; } = subjectId;

    public AvatarObservationWriteResult? Result { get; } = result;

    public Exception? Error { get; } = error;
}
