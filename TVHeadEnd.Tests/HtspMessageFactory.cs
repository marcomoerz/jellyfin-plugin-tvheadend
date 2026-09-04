using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.Tests;

/// <summary>
/// Builds messages the way they actually reach the data helpers: through the wire format.
/// </summary>
/// <remarks>
/// Round tripping matters here. Fields written with putField keep their CLR type, while fields
/// that came off the socket carry whatever the deserialiser produced. Testing against the latter
/// is the only way to catch a helper that reads a field with the wrong accessor.
/// </remarks>
internal static class HtspMessageFactory
{
    /// <summary>Serialises and parses a message, so it carries wire types.</summary>
    public static HTSMessage Wire(HTSMessage message) =>
        HTSMessage.parse(message.BuildBytes(), NullLogger<HTSMessage>.Instance);

    /// <summary>Builds a DVR entry as TVHeadend would send it.</summary>
    public static HTSMessage DvrEntry(
        int id,
        string state,
        string title = "Some Recording",
        int channel = 1,
        long? eventId = null,
        string? autorecId = null,
        string? error = null,
        string? description = null,
        string? subtitle = null)
    {
        DateTime start = new DateTime(2026, 9, 4, 20, 15, 0, DateTimeKind.Utc);

        HTSMessage message = new HTSMessage();
        message.Method = "dvrEntryAdd";
        message.putField("id", id);
        message.putField("channel", channel);
        message.putField("state", state);
        message.putField("title", title);
        message.putField("start", ToUnix(start));
        message.putField("stop", ToUnix(start.AddMinutes(30)));
        message.putField("startExtra", 2L);
        message.putField("stopExtra", 5L);
        message.putField("priority", 2);

        if (eventId.HasValue)
        {
            message.putField("eventId", eventId.Value);
        }

        if (autorecId is not null)
        {
            message.putField("autorecId", autorecId);
        }

        if (error is not null)
        {
            message.putField("error", error);
        }

        if (description is not null)
        {
            message.putField("description", description);
        }

        if (subtitle is not null)
        {
            message.putField("subtitle", subtitle);
        }

        return Wire(message);
    }

    private static long ToUnix(DateTime utc) =>
        (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
}
