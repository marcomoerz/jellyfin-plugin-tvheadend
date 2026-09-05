using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;

namespace TVHeadEnd;

public class AccessTicketHandler
{
    private readonly ILogger<AccessTicketHandler> _logger;

    private readonly HTSConnectionHandler _htsConnectionHandler;
    private readonly string _ticketItemType;
    private readonly TimeSpan _requestTimeout;
    private readonly int _requestRetries;
    private readonly TimeSpan _ticketLifeSpan;

    private volatile int _ticketIdSequence;

    public enum TicketType : byte { Channel, Recording };

    public record Ticket
    {
        public required string Id { get; init; }
        public required string Path { get; init; }
        public required string TicketParam { get; init; }
        public string Url => $"{Path}?ticket={TicketParam}";
        public DateTime Expires { get; init; }
    }

    private readonly ConcurrentDictionary<string, Task<Ticket>> _ticketCache = new();

    internal AccessTicketHandler(
        ILoggerFactory loggerFactory, HTSConnectionHandler htsConnectionHandler,
        TimeSpan requestTimeout, int requestRetries, TimeSpan ticketLifeSpan, TicketType ticketType)
    {
        _logger = loggerFactory.CreateLogger<AccessTicketHandler>();
        _htsConnectionHandler = htsConnectionHandler;
        _requestTimeout = requestTimeout;
        _requestRetries = requestRetries;
        _ticketLifeSpan = ticketLifeSpan;

        _ticketItemType = ticketType switch
        {
            TicketType.Channel => "channelId",
            TicketType.Recording => "dvrId",
            _ => throw new ArgumentException("undefined ticketType")
        };
    }

    public async Task<Ticket> GetTicket(string itemId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        Ticket? ticket = null;

        while (_ticketCache.TryGetValue(itemId, out var ticketTask))
        {
            try
            {
                ticket = await ticketTask;
            }
            catch
            {
                // The cached request failed (e.g. TVH unreachable). Drop it so
                // the next attempt can request a fresh ticket, otherwise this
                // item could never be played again without a restart.
                _ticketCache.TryRemove(new KeyValuePair<string, Task<Ticket>>(itemId, ticketTask));
                ticket = null;
                break;
            }

            if (ticket.Expires > now)
            {
                return ticket; // non-expired ticket from cache
            }

            _logger.LogDebug("[TVHclient] AccessTicketHandler.GetAccessTicket: Cache expired for {ItemType}={ItemId}. Revalidating ticket (#{TicketId})", _ticketItemType, itemId, ticket.Id);
            _ticketCache.TryRemove(new KeyValuePair<string, Task<Ticket>>(itemId, ticketTask));
        }

        var newTicketTask = _ticketCache.GetOrAdd(itemId, _ => GetTicketRecord(itemId, cancellationToken, ticket, now));
        try
        {
            return await newTicketTask;
        }
        catch
        {
            _ticketCache.TryRemove(new KeyValuePair<string, Task<Ticket>>(itemId, newTicketTask));
            throw;
        }
    }

    private Task<Ticket> GetTicketRecord(string itemId, CancellationToken cancellation, Ticket? currentRecord, DateTime now)
    {
        return RequestTicket(itemId, cancellation).ContinueWith(ticketTask =>
        {
            var response = ticketTask.Result;
            var path = response.getString("path");
            var ticket = response.getString("ticket");

            var id = (currentRecord != null && path == currentRecord.Path && ticket == currentRecord.TicketParam)
                ? currentRecord.Id
                : $"{NextTicketId()}";

            if (id != currentRecord?.Id)
            {
                _logger.LogInformation("[TVHclient] AccessTicketHandler.GetAccessTicket: New ticket (#{TicketId}) created for {ItemType}={ItemId}", id, _ticketItemType, itemId);
            }

            return new Ticket()
            {
                Id = id,
                Path = path,
                TicketParam = ticket,
                Expires = now + _ticketLifeSpan,
            };
        }, cancellation);
    }

    private async Task<HTSMessage> RequestTicket(string itemId, CancellationToken cancellation)
    {
        var request = new HTSMessage { Method = "getTicket" };
        request.putField(_ticketItemType, itemId);

        for (int attempt = 1, lastAttempt = 1 + _requestRetries;
             attempt <= lastAttempt && !cancellation.IsCancellationRequested;
             attempt++)
        {
            Result<HTSMessage, HtspError> result = await _htsConnectionHandler
                .SendRequestAsync(request, _requestTimeout * attempt, cancellation)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                return result.Value;
            }

            // Only a timeout earns another attempt with a longer grace period. A refusal would
            // just be refused again, and retrying only delays the error the user has to see.
            if (result.Error is not HtspError.Timeout)
            {
                throw new InvalidOperationException(
                    $"Obtaining a playback ticket from TVHeadend failed: {result.Error.Describe()}");
            }
        }

        _logger.LogError("[TVHclient] AccessTicketHandler.GetAccessTicket: can't obtain playback authentication ticket from TVH because the timeout was reached");

        throw new TimeoutException("Obtaining playback authentication ticket from TVH caused a network timeout");
    }

    private int NextTicketId()
    {
        int id;
        while ((id = Interlocked.Increment(ref _ticketIdSequence)) < 0)
        {
            _ticketIdSequence = Math.Max(0, _ticketIdSequence);
        }

        return id;
    }
}
