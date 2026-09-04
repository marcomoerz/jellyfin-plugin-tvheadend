using TVHeadEnd.HTSP;

namespace TVHeadEnd.Tests;

/// <summary>Captures what the connection reports, so tests can assert on it.</summary>
internal sealed class RecordingListener : HTSConnectionListener
{
    private readonly List<HTSMessage> _messages = new();
    private int _errorCount;

    /// <summary>When set, onMessage throws this — used to prove a bad listener cannot kill the connection.</summary>
    public Exception? ThrowOnMessage { get; set; }

    public int ErrorCount => Volatile.Read(ref _errorCount);

    public IReadOnlyList<HTSMessage> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public void onMessage(HTSMessage response)
    {
        lock (_messages)
        {
            _messages.Add(response);
        }

        if (ThrowOnMessage is not null)
        {
            throw ThrowOnMessage;
        }
    }

    public void onError(Exception ex)
    {
        Interlocked.Increment(ref _errorCount);
    }

    public async Task WaitForMessagesAsync(int count, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (Messages.Count < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"expected {count} messages, saw {Messages.Count}");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public async Task WaitForErrorsAsync(int count, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (ErrorCount < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"expected {count} errors, saw {ErrorCount}");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
