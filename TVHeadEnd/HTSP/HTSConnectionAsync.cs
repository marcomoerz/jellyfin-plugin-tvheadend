using System;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Helper;

namespace TVHeadEnd.HTSP
{

    

    public sealed class HTSConnectionAsync : IAsyncDisposable
    {
        /// <summary>Guards against a corrupt length prefix turning into a huge allocation.</summary>
        private const long MaxMessageBytes = 64 * 1024 * 1024;

        private readonly HTSConnectionListener _listener;
        private readonly string _clientName;
        private readonly string _clientVersion;
        private readonly ILogger<HTSConnectionAsync> _logger;
        private readonly ILogger<HTSMessage> _messageLogger;

        private readonly Channel<HTSMessage> _outgoing;
        private readonly Channel<HTSMessage> _incoming;

        /// <summary>
        /// Requests still waiting for their reply, keyed by sequence number. Concurrent because
        /// callers register from arbitrary threads while the dispatch loop completes them.
        /// </summary>
        private readonly ConcurrentDictionary<int, TaskCompletionSource<HTSMessage>> _pending;

        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

        private int _sequenceNumber;
        private int _faultSignalled;
        private int _disposed;

        private NetworkStream? _stream;
        private Task? _sendLoop;
        private Task? _receiveLoop;
        private Task? _dispatchLoop;

        private int _serverProtocolVersion = -1;
        private string _servername = "n/a";
        private string _serverversion = "n/a";
        private string _diskSpace = "n/a";

        public HTSConnectionAsync(HTSConnectionListener listener, string clientName, string clientVersion, ILoggerFactory loggerFactory)
        {
            _listener = listener;
            _clientName = clientName;
            _clientVersion = clientVersion;
            _logger = loggerFactory.CreateLogger<HTSConnectionAsync>();
            _messageLogger = loggerFactory.CreateLogger<HTSMessage>();

            // A single reader drains each channel, and nothing benefits from bounding them: the
            // socket is the real backpressure.
            UnboundedChannelOptions options = new UnboundedChannelOptions { SingleReader = true };
            _outgoing = Channel.CreateUnbounded<HTSMessage>(options);
            _incoming = Channel.CreateUnbounded<HTSMessage>(options);

            _pending = new ConcurrentDictionary<int, TaskCompletionSource<HTSMessage>>();
        }

        /// <summary>Gets a value indicating whether this connection has failed and must be replaced.</summary>
        public bool IsFaulted => Volatile.Read(ref _faultSignalled) != 0;

        public int GetServerProtocolVersion() => _serverProtocolVersion;

        public string GetServername() => _servername;

        public string GetServerversion() => _serverversion;

        public string GetDiskspace() => _diskSpace;

        /// <summary>
        /// Opens the socket and starts the pumps. Makes exactly one attempt: retry policy belongs
        /// to the caller, which knows whether anyone is still waiting for an answer.
        /// </summary>
        public async Task ConnectAsync(string hostname, int port, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(hostname, port, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _logger.LogError("[TVHclient] HTSConnectionAsync.ConnectAsync: failed to connect to {host}:{port}", hostname, port);
                socket.Dispose();
                throw;
            }

            _logger.LogDebug("[TVHclient] HTSConnectionAsync.ConnectAsync: connected to {host}:{port}", hostname, port);

            NetworkStream stream = new NetworkStream(socket, ownsSocket: true);
            _stream = stream;

            CancellationToken token = _shutdown.Token;
            // CancellationToken.None is deliberate: the loops are expected to run until the connection is closed,
            // and the cancellation token is used only to break out of the async waits. The loops themselves are not cancellable.
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(stream, token), CancellationToken.None);
            _sendLoop = Task.Run(() => SendLoopAsync(stream, token), CancellationToken.None);
            _dispatchLoop = Task.Run(() => DispatchLoopAsync(token), CancellationToken.None);
        }

        /// <summary>
        /// Sends a message and waits for its reply.
        /// </summary>
        /// <remarks>
        /// The reply is matched by sequence number, which is written into the message before
        /// sending. The caller must not reuse the message object afterwards.
        /// </remarks>
        public async Task<Result<HTSMessage, HtspError>> SendRequestAsync(HTSMessage message, TimeSpan timeout, CancellationToken cancellationToken)
        {
            int sequenceNumber = NextSequenceNumber();
            TaskCompletionSource<HTSMessage> pending =
                new TaskCompletionSource<HTSMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Register before sending. The reply can come back faster than this thread is
            // rescheduled, and an unmatched reply is dropped — the caller would wait forever.
            _pending[sequenceNumber] = pending;
            try
            {
                message.putField("seq", sequenceNumber);

                try
                {
                    await _outgoing.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                    return await pending.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return Classify(ex, timeout, cancellationToken);
                }
            }
            finally
            {
                // Runs on every path — success, timeout, cancellation, connection loss — so
                // abandoned entries cannot accumulate.
                _pending.TryRemove(sequenceNumber, out _);
            }
        }

        /// <summary>
        /// Sends a message that has no reply to correlate. Deliberately carries no sequence
        /// number, so TVHeadend does not echo one back for nobody.
        /// </summary>
        public async ValueTask<Result<Unit, HtspError>> PostAsync(HTSMessage message, CancellationToken cancellationToken)
        {
            try
            {
                await _outgoing.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                return Unit.Value;
            }
            catch (Exception ex)
            {
                return Classify(ex, timeout: null, cancellationToken);
            }
        }

        /// <summary>
        /// Turns the exceptions the plumbing throws into the error categories callers act on.
        /// </summary>
        private static HtspError Classify(Exception exception, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            return exception switch
            {
                OperationCanceledException when cancellationToken.IsCancellationRequested => new HtspError.Cancelled(),
                TimeoutException => new HtspError.Timeout(timeout ?? System.Threading.Timeout.InfiniteTimeSpan),
                ChannelClosedException => new HtspError.ConnectionClosed(),
                ObjectDisposedException => new HtspError.ConnectionClosed(),
                _ => new HtspError.Transport(exception.Message),
            };
        }

        /// <summary>
        /// Performs the HTSP login handshake and subscribes to the metadata stream.
        /// </summary>
        /// <remarks>
        /// Three requests that each depend on the previous one: hello returns the challenge the
        /// password is salted with, authenticate answers it, and only then may we subscribe.
        /// Chained rather than nested so the failure of any step short circuits the rest.
        /// </remarks>
        public Task<Result<Unit, HtspError>> AuthenticateAsync(string username, string password, TimeSpan timeout, CancellationToken cancellationToken)
        {
            HTSMessage helloMessage = new HTSMessage();
            helloMessage.Method = "hello";
            helloMessage.putField("clientname", _clientName);
            helloMessage.putField("clientversion", _clientVersion);
            helloMessage.putField("htspversion", HTSMessage.HTSP_VERSION);
            helloMessage.putField("username", username);

            return SendRequestAsync(helloMessage, timeout, cancellationToken)
                .TapErrorAsync(error => _logger.LogError(
                    "[TVHclient] HTSConnectionAsync: hello failed: {error}", error.Describe()))
                .AndThenAsync(helloResponse => Authenticate(helloResponse, username, password, timeout, cancellationToken))
                .AndThenAsync(_ => SubscribeToMetadata(username, timeout, cancellationToken));
        }

        /// <summary>Answers the server challenge with the salted password digest.</summary>
        private Task<Result<HTSMessage, HtspError>> Authenticate(
            HTSMessage helloResponse, string username, string password, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _serverProtocolVersion = ReadField(helloResponse, "htspversion", m => m.getInt("htspversion"), -1);
            _servername = ReadField(helloResponse, "servername", m => m.getString("servername"), "n/a");
            _serverversion = ReadField(helloResponse, "serverversion", m => m.getString("serverversion"), "n/a");

            byte[] salt = ReadField(helloResponse, "challenge", m => m.getByteArray("challenge"), []);

            HTSMessage authMessage = new HTSMessage();
            authMessage.Method = "authenticate";
            authMessage.putField("username", username);
            authMessage.putField("digest", SHA1helper.GenerateSaltedSHA1(password, salt));

            return SendRequestAsync(authMessage, timeout, cancellationToken)
                .TapErrorAsync(error => _logger.LogError(
                    "[TVHclient] HTSConnectionAsync: authenticate failed: {error}", error.Describe()))
                .AndThen(authResponse => RejectIfDenied(authResponse, username));
        }

        /// <summary>
        /// TVHeadend answers a wrong password with noaccess rather than an error field.
        /// </summary>
        private Result<HTSMessage, HtspError> RejectIfDenied(HTSMessage authResponse, string username)
        {
            if (authResponse.getInt("noaccess", 0) != 1)
            {
                return authResponse;
            }

            _logger.LogError(
                "[TVHclient] HTSConnectionAsync: access denied for user '{user}'", username);
            return new HtspError.Rejected($"access denied for user '{username}'");
        }

        /// <summary>
        /// Subscribes to the metadata stream. Its messages arrive without a sequence number and
        /// are routed to the listener.
        /// </summary>
        private async Task<Result<Unit, HtspError>> SubscribeToMetadata(
            string username, TimeSpan timeout, CancellationToken cancellationToken)
        {
            // Disk space is decoration for the settings page. A server that will not report it is
            // still perfectly usable, so this must not fail the login.
            (await ReadDiskSpaceAsync(timeout, cancellationToken).ConfigureAwait(false))
                .TapError(error => _logger.LogError(
                    "[TVHclient] HTSConnectionAsync: could not read disk space: {error}", error.Describe()));

            HTSMessage enableAsyncMetadata = new HTSMessage();
            enableAsyncMetadata.Method = "enableAsyncMetadata";

            return (await PostAsync(enableAsyncMetadata, cancellationToken).ConfigureAwait(false))
                .TapError(error => _logger.LogError(
                    "[TVHclient] HTSConnectionAsync: could not enable async metadata: {error}", error.Describe()))
                .Tap(_ => _logger.LogDebug(
                    "[TVHclient] HTSConnectionAsync.AuthenticateAsync: authenticated as '{user}'", username));
        }

        private async Task<Result<Unit, HtspError>> ReadDiskSpaceAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            const long BytesPerGiga = 1024 * 1024 * 1024;

            HTSMessage request = new HTSMessage();
            request.Method = "getDiskSpace";

            return (await SendRequestAsync(request, timeout, cancellationToken).ConfigureAwait(false)).AndThen(
                diskSpaceResponse => {
                
                long free = ReadField(diskSpaceResponse, "freediskspace", m => m.getLong("freediskspace"), -1L) / BytesPerGiga;
                long total = ReadField(diskSpaceResponse, "totaldiskspace", m => m.getLong("totaldiskspace"), -1L) / BytesPerGiga;
                if (free < 0 || total < 0)
                {
                    _logger.LogDebug("[TVHclient] HTSConnectionAsync.ReadDiskSpaceAsync: invalid disk space values received from server");
                    return Result<Unit, HtspError>.Failure(new HtspError.Rejected("invalid disk space values received from server"));
                }
                _diskSpace = free + "GB / " + total + "GB";
                _logger.LogDebug("[TVHclient] HTSConnectionAsync.ReadDiskSpaceAsync: disk space is {diskSpace}", _diskSpace);
                return Unit.Value;
            });
        }

        private T ReadField<T>(HTSMessage message, string field, Func<HTSMessage, T> read, T fallback)
        {
            if (message.containsField(field))
            {
                return read(message);
            }

            _logger.LogDebug(
                "[TVHclient] HTSConnectionAsync: response is missing field '{field}' - htsp incorrectly implemented by tvheadend",
                field);
            return fallback;
        }

        private int NextSequenceNumber()
        {
            // Interlocked because callers race; masked to stay positive across the wrap.
            return Interlocked.Increment(ref _sequenceNumber) & 0x7FFFFFFF;
        }

        private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] lengthPrefix = new byte[4];

            try
            {
                while (true)
                {
                    await stream.ReadExactlyAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);

                    long payloadLength = HTSMessage.uIntToLong(lengthPrefix[0], lengthPrefix[1], lengthPrefix[2], lengthPrefix[3]);
                    if (payloadLength < 0 || payloadLength > MaxMessageBytes)
                    {
                        throw new InvalidDataException(
                            $"HTSP frame announces {payloadLength} bytes, which is out of range. The stream is out of sync.");
                    }

                    // HTSMessage.parse expects the length prefix to still be there.
                    byte[] frame = new byte[payloadLength + 4];
                    Array.Copy(lengthPrefix, frame, 4);
                    await stream.ReadExactlyAsync(frame.AsMemory(4, (int)payloadLength), cancellationToken).ConfigureAwait(false);

                    HTSMessage? message = HTSMessage.parse(frame, _messageLogger);
                    if (message != null)
                    {
                        await _incoming.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
            finally
            {
                _incoming.Writer.TryComplete();
            }
        }

        private async Task SendLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (HTSMessage message in _outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    byte[] data = message.BuildBytes();
                    await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }

        private async Task DispatchLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (HTSMessage message in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (message.containsField("seq"))
                    {
                        int sequenceNumber = message.getInt("seq");
                        if (_pending.TryRemove(sequenceNumber, out TaskCompletionSource<HTSMessage>? pending))
                        {
                            pending.TrySetResult(message);
                        }
                        else
                        {
                            // The caller timed out or went away. Not worth more than a debug line.
                            _logger.LogDebug(
                                "[TVHclient] HTSConnectionAsync: reply for seq '{seq}' has no waiting caller", sequenceNumber);
                        }

                        continue;
                    }

                    // Unsolicited metadata update. A listener that throws is a bug in the
                    // listener, not a reason to tear down a healthy connection.
                    try
                    {
                        _listener?.onMessage(message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[TVHclient] HTSConnectionAsync: listener failed to handle a message");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }

        /// <summary>
        /// Records the first failure and tells the owner once. Every pump can fail at the same
        /// instant when a socket dies; without this guard each of them would start its own
        /// reconnect.
        /// </summary>
        private void Fault(Exception ex)
        {
            if (Interlocked.Exchange(ref _faultSignalled, 1) != 0)
            {
                return;
            }

            _logger.LogError(ex, "[TVHclient] HTSConnectionAsync: connection failed");

            _shutdown.Cancel();
            _outgoing.Writer.TryComplete(ex);

            // Nobody will ever answer these now. Failing them turns an eternal wait into an error
            // the caller can report.
            foreach (var entry in _pending)
            {
                if (_pending.TryRemove(entry.Key, out TaskCompletionSource<HTSMessage>? pending))
                {
                    pending.TrySetException(ex);
                }
            }

            try
            {
                _listener?.onError(ex);
            }
            catch (Exception listenerFailure)
            {
                _logger.LogError(listenerFailure, "[TVHclient] HTSConnectionAsync: listener failed to handle the error");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _shutdown.Cancel();
            _outgoing.Writer.TryComplete();

            ObjectDisposedException disposed = new ObjectDisposedException(nameof(HTSConnectionAsync));
            foreach (var entry in _pending)
            {
                if (_pending.TryRemove(entry.Key, out TaskCompletionSource<HTSMessage>? pending))
                {
                    pending.TrySetException(disposed);
                }
            }

            try
            {
                Task?[] loops = new[] { _sendLoop, _receiveLoop, _dispatchLoop };
                foreach (Task? loop in loops)
                {
                    if (loop != null)
                    {
                        await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[TVHclient] HTSConnectionAsync: pumps did not stop cleanly");
            }

            _stream?.Dispose();
            _shutdown.Dispose();
        }
    }
}
