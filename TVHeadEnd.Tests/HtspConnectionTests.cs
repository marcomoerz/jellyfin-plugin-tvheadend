using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.Tests;

/// <summary>
/// Exercises the connection layer against a real socket. These cover the failure modes the
/// previous implementation had: replies matched to the wrong caller, callers waiting forever,
/// and a dying socket triggering one reconnect per worker.
/// </summary>
/// <remarks>
/// The connection reports failure as a <see cref="Result{T, TError}"/> rather than by throwing,
/// so every assertion here goes through IsSuccess instead of Assert.Throws.
/// </remarks>
public class HtspConnectionTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static HTSConnectionAsync CreateConnection(RecordingListener listener) =>
        new(listener, "test-client", "20", NullLoggerFactory.Instance);

    private static HTSMessage Request(string method, int payload)
    {
        HTSMessage message = new HTSMessage();
        message.Method = method;
        message.putField("payload", payload);
        return message;
    }

    private static HTSMessage Echo(HTSMessage request)
    {
        HTSMessage reply = new HTSMessage();
        reply.putField("echo", request.getInt("payload"));
        return reply;
    }

    [Fact]
    public async Task SendRequest_ReturnsTheMatchingReply()
    {
        await using FakeTvheadendServer server = new(r => Task.FromResult<HTSMessage?>(Echo(r)));

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        Result<HTSMessage, HtspError> result =
            await connection.SendRequestAsync(Request("test", 42), ShortTimeout, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Describe());
        Assert.Equal(42, result.Value.getInt("echo"));
    }

    /// <summary>
    /// Regression test for the correlation race: replies used to be matched through an
    /// unsynchronised dictionary, and the handler was registered only after the message had gone
    /// out. Under load that mixed up callers or lost replies entirely.
    /// </summary>
    [Fact]
    public async Task ConcurrentRequests_EachCallerGetsItsOwnReply()
    {
        Random random = new(1234);

        await using FakeTvheadendServer server = new(async request =>
        {
            int payload = request.getInt("payload");

            int delay;
            lock (random)
            {
                delay = random.Next(0, 25);
            }

            // Jitter, so replies come back out of order.
            await Task.Delay(delay);

            HTSMessage reply = new HTSMessage();
            reply.putField("echo", payload);
            return (HTSMessage?)reply;
        });

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        const int Count = 200;
        Task<Result<HTSMessage, HtspError>>[] inFlight = Enumerable.Range(0, Count)
            .Select(i => connection.SendRequestAsync(Request("test", i), ShortTimeout, CancellationToken.None))
            .ToArray();

        Result<HTSMessage, HtspError>[] responses = await Task.WhenAll(inFlight);

        for (int i = 0; i < Count; i++)
        {
            Assert.True(responses[i].IsSuccess, responses[i].Error?.Describe());
            Assert.Equal(i, responses[i].Value.getInt("echo"));
        }

        Assert.Equal(0, listener.ErrorCount);
    }

    [Fact]
    public async Task SendRequest_WithoutReply_TimesOutInsteadOfHanging()
    {
        await using FakeTvheadendServer server = new(_ => Task.FromResult<HTSMessage?>(null));

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        Result<HTSMessage, HtspError> result = await connection.SendRequestAsync(
            Request("test", 1), TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.IsType<HtspError.Timeout>(result.Error);
    }

    /// <summary>
    /// A reply that arrives after its caller gave up must be dropped quietly and must not
    /// disturb the next request.
    /// </summary>
    [Fact]
    public async Task LateReply_DoesNotBreakTheNextRequest()
    {
        await using FakeTvheadendServer server = new(async request =>
        {
            int payload = request.getInt("payload");
            if (payload == 1)
            {
                await Task.Delay(600);
            }

            HTSMessage reply = new HTSMessage();
            reply.putField("echo", payload);
            return (HTSMessage?)reply;
        });

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        Result<HTSMessage, HtspError> abandoned = await connection.SendRequestAsync(
            Request("slow", 1), TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.False(abandoned.IsSuccess);

        await Task.Delay(800);

        Result<HTSMessage, HtspError> result =
            await connection.SendRequestAsync(Request("fast", 2), ShortTimeout, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Describe());
        Assert.Equal(2, result.Value.getInt("echo"));
    }

    [Fact]
    public async Task SendRequest_Cancelled_ReportsFailure()
    {
        await using FakeTvheadendServer server = new(_ => Task.FromResult<HTSMessage?>(null));

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        using CancellationTokenSource cts = new();
        Task<Result<HTSMessage, HtspError>> pending =
            connection.SendRequestAsync(Request("test", 1), ShortTimeout, cts.Token);

        await cts.CancelAsync();

        Result<HTSMessage, HtspError> result = await pending;

        Assert.False(result.IsSuccess);
        Assert.IsType<HtspError.Cancelled>(result.Error);
    }

    /// <summary>
    /// When the server disappears, a caller must get an error rather than block forever. The old
    /// implementation parked the caller on a queue that never timed out, leaking a dedicated
    /// thread with it.
    /// </summary>
    [Fact]
    public async Task ConnectionLost_PendingRequestFails()
    {
        FakeTvheadendServer server = new(async _ =>
        {
            await Task.Delay(Timeout.Infinite);
            return (HTSMessage?)null;
        });

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        Task<Result<HTSMessage, HtspError>> pending =
            connection.SendRequestAsync(Request("test", 1), TimeSpan.FromSeconds(30), CancellationToken.None);

        await server.WaitForClientAsync(ShortTimeout);
        server.DropConnection();

        // Must resolve well inside the request timeout, i.e. from the fault and not the clock.
        Result<HTSMessage, HtspError> result = await pending.WaitAsync(ShortTimeout);

        Assert.False(result.IsSuccess);
        Assert.True(connection.IsFaulted);

        await server.DisposeAsync();
    }

    /// <summary>
    /// Every pump can fail at the same moment. The owner must hear about it once, otherwise each
    /// of them starts its own reconnect.
    /// </summary>
    [Fact]
    public async Task ConnectionLost_ListenerIsNotifiedExactlyOnce()
    {
        FakeTvheadendServer server = new(_ => Task.FromResult<HTSMessage?>(null));

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);
        await server.WaitForClientAsync(ShortTimeout);

        _ = connection.SendRequestAsync(Request("test", 1), TimeSpan.FromSeconds(30), CancellationToken.None);

        server.DropConnection();
        await listener.WaitForErrorsAsync(1, ShortTimeout);

        // Let further failures pile up if the guard does not hold.
        await Task.Delay(500);

        Assert.Equal(1, listener.ErrorCount);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task UnsolicitedMessage_ReachesTheListener()
    {
        await using FakeTvheadendServer server = new(_ => Task.FromResult<HTSMessage?>(null));

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);
        await server.WaitForClientAsync(ShortTimeout);

        HTSMessage update = new HTSMessage();
        update.Method = "channelAdd";
        update.putField("channelId", 7);
        await server.SendUnsolicitedAsync(update);

        await listener.WaitForMessagesAsync(1, ShortTimeout);
        Assert.Equal("channelAdd", listener.Messages[0].Method);
    }

    /// <summary>A broken data helper is a bug in the helper, not a reason to drop the connection.</summary>
    [Fact]
    public async Task ListenerThatThrows_DoesNotFaultTheConnection()
    {
        await using FakeTvheadendServer server = new(r => Task.FromResult<HTSMessage?>(Echo(r)));

        RecordingListener listener = new() { ThrowOnMessage = new InvalidOperationException("helper is broken") };
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);
        await server.WaitForClientAsync(ShortTimeout);

        HTSMessage update = new HTSMessage();
        update.Method = "channelAdd";
        await server.SendUnsolicitedAsync(update);
        await listener.WaitForMessagesAsync(1, ShortTimeout);

        Assert.False(connection.IsFaulted);

        Result<HTSMessage, HtspError> result =
            await connection.SendRequestAsync(Request("test", 99), ShortTimeout, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Describe());
        Assert.Equal(99, result.Value.getInt("echo"));
    }

    [Fact]
    public async Task Dispose_ReleasesPendingRequests()
    {
        await using FakeTvheadendServer server = new(async _ =>
        {
            await Task.Delay(Timeout.Infinite);
            return (HTSMessage?)null;
        });

        RecordingListener listener = new();
        HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        Task<Result<HTSMessage, HtspError>> pending =
            connection.SendRequestAsync(Request("test", 1), TimeSpan.FromSeconds(30), CancellationToken.None);
        await server.WaitForClientAsync(ShortTimeout);

        await connection.DisposeAsync();

        Result<HTSMessage, HtspError> result = await pending.WaitAsync(ShortTimeout);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Connect_ToClosedPort_ReportsFailureInsteadOfLooping()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            connection.ConnectAsync("127.0.0.1", deadPort, CancellationToken.None).WaitAsync(ShortTimeout));
    }

    /// <summary>
    /// The full login handshake: hello returns the challenge, authenticate answers it, and the
    /// metadata subscription goes out last.
    /// </summary>
    [Fact]
    public async Task Authenticate_CompletesTheHandshake()
    {
        List<string> methods = new();

        await using FakeTvheadendServer server = new(request =>
        {
            lock (methods)
            {
                methods.Add(request.Method);
            }

            HTSMessage reply = new HTSMessage();
            switch (request.Method)
            {
                case "hello":
                    reply.putField("htspversion", 20);
                    reply.putField("servername", "TestTVH");
                    reply.putField("serverversion", "4.3");
                    reply.putField("challenge", new byte[] { 1, 2, 3, 4 });
                    break;
                case "getDiskSpace":
                    reply.putField("freediskspace", 50L * 1024 * 1024 * 1024);
                    reply.putField("totaldiskspace", 200L * 1024 * 1024 * 1024);
                    break;
            }

            return Task.FromResult<HTSMessage?>(reply);
        });

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        Result<Unit, HtspError> result =
            await connection.AuthenticateAsync("user", "secret", ShortTimeout, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Describe());
        Assert.Equal(20, connection.GetServerProtocolVersion());
        Assert.Equal("TestTVH", connection.GetServername());
        Assert.Equal("50GB / 200GB", connection.GetDiskspace());

        lock (methods)
        {
            Assert.Contains("hello", methods);
            Assert.Contains("authenticate", methods);
        }
    }

    [Fact]
    public async Task Authenticate_WithWrongPassword_Fails()
    {
        await using FakeTvheadendServer server = new(request =>
        {
            HTSMessage reply = new HTSMessage();
            if (request.Method == "hello")
            {
                reply.putField("challenge", new byte[] { 1, 2, 3, 4 });
            }
            else if (request.Method == "authenticate")
            {
                reply.putField("noaccess", 1);
            }

            return Task.FromResult<HTSMessage?>(reply);
        });

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        Result<Unit, HtspError> result =
            await connection.AuthenticateAsync("user", "wrong", ShortTimeout, CancellationToken.None);

        Assert.False(result.IsSuccess);
        HtspError.Rejected rejected = Assert.IsType<HtspError.Rejected>(result.Error);
        Assert.Contains("access denied", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Disk space is decoration for the settings page. A server that will not report it must
    /// still be usable.
    /// </summary>
    [Fact]
    public async Task Authenticate_SucceedsEvenWithoutDiskSpace()
    {
        await using FakeTvheadendServer server = new(request =>
        {
            HTSMessage reply = new HTSMessage();
            if (request.Method == "hello")
            {
                reply.putField("challenge", new byte[] { 1, 2, 3, 4 });
            }

            // getDiskSpace answers without the fields.
            return Task.FromResult<HTSMessage?>(reply);
        });

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        Result<Unit, HtspError> result =
            await connection.AuthenticateAsync("user", "secret", ShortTimeout, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Describe());
    }

    /// <summary>A failing first step must short circuit the rest of the chain.</summary>
    [Fact]
    public async Task Authenticate_WhenHelloTimesOut_ReportsThatStep()
    {
        await using FakeTvheadendServer server = new(_ => Task.FromResult<HTSMessage?>(null));

        RecordingListener listener = new();
        await using HTSConnectionAsync connection = CreateConnection(listener);
        await connection.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None);

        Result<Unit, HtspError> result = await connection.AuthenticateAsync(
            "user", "secret", TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(result.IsSuccess);

        // The category survives the whole chain: it is still a timeout, not a generic failure.
        Assert.IsType<HtspError.Timeout>(result.Error);
    }
}
