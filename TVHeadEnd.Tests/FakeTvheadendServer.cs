using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.Tests;

/// <summary>
/// A minimal HTSP server on loopback, so the connection layer can be exercised over a real
/// socket instead of a mock. Speaks just enough of the protocol: 4 byte big endian length
/// prefix followed by a serialised message.
/// </summary>
internal sealed class FakeTvheadendServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<HTSMessage, Task<HTSMessage?>> _respond;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _acceptLoop;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _requestCount;

    /// <param name="respond">
    /// Builds the reply for a request, or returns null to stay silent. Runs on its own task, so a
    /// slow responder does not hold up the next request — that is how out of order replies are
    /// produced.
    /// </param>
    public FakeTvheadendServer(Func<HTSMessage, Task<HTSMessage?>> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    /// <summary>Number of requests received so far.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Kills the connection the way a crashing server would.</summary>
    public void DropConnection()
    {
        try
        {
            _client?.Client.Close();
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>Pushes a message that answers no request, like TVHeadend's metadata updates.</summary>
    public async Task SendUnsolicitedAsync(HTSMessage message)
    {
        NetworkStream stream = _stream ?? throw new InvalidOperationException("no client connected yet");
        await WriteAsync(stream, message).ConfigureAwait(false);
    }

    /// <summary>Waits until a client has connected, so tests do not race the accept loop.</summary>
    public async Task WaitForClientAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (_stream is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("no client connected");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            _stream = _client.GetStream();

            byte[] lengthPrefix = new byte[4];
            while (!_shutdown.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(lengthPrefix, _shutdown.Token).ConfigureAwait(false);
                long length = HTSMessage.uIntToLong(lengthPrefix[0], lengthPrefix[1], lengthPrefix[2], lengthPrefix[3]);

                byte[] frame = new byte[length + 4];
                Array.Copy(lengthPrefix, frame, 4);
                await _stream.ReadExactlyAsync(frame.AsMemory(4, (int)length), _shutdown.Token).ConfigureAwait(false);

                HTSMessage? request = HTSMessage.parse(frame, NullLogger<HTSMessage>.Instance);
                if (request is null)
                {
                    continue;
                }

                Interlocked.Increment(ref _requestCount);

                _ = Task.Run(() => RespondAsync(request));
            }
        }
        catch
        {
            // Client gone or shutting down; nothing useful to do in a test double.
        }
    }

    private async Task RespondAsync(HTSMessage request)
    {
        try
        {
            HTSMessage? reply = await _respond(request).ConfigureAwait(false);
            if (reply is null || _stream is null)
            {
                return;
            }

            if (request.containsField("seq"))
            {
                reply.putField("seq", request.getInt("seq"));
            }

            await WriteAsync(_stream, reply).ConfigureAwait(false);
        }
        catch
        {
            // The test asserts on the client side.
        }
    }

    private async Task WriteAsync(NetworkStream stream, HTSMessage message)
    {
        // One writer at a time, otherwise concurrent replies would interleave their frames.
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[] data = message.BuildBytes();
            await stream.WriteAsync(data).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            _client?.Dispose();
            _listener.Stop();
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Best effort teardown.
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }
}
