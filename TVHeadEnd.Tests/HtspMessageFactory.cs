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
    public static HTSMessage Wire(HTSMessage message)
    {
        // A message this test built must always parse back; a null here is a defect in the
        // serialiser, not an expected outcome.
        return HTSMessage.parse(message.BuildBytes(), NullLogger<HTSMessage>.Instance)
            ?? throw new InvalidOperationException("the message did not survive serialisation");
    }

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
        string? subtitle = null,
        List<object>? files = null)
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

        if (files is not null)
        {
            message.putField("files", files);
        }

        return Wire(message);
    }

    /// <summary>
    /// Builds the "files" list TVHeadend attaches to a finished recording, as assembled in
    /// dvr_rec.c: one entry per file, each with the stream descriptions and the size.
    /// </summary>
    public static List<object> RecordedFiles(
        long sizeBytes,
        long startUnix,
        long stopUnix,
        params Dictionary<string, object>[] streams)
    {
        Dictionary<string, object> file = new()
        {
            ["filename"] = "/recordings/example.ts",
            ["size"] = sizeBytes,
            ["start"] = startUnix,
            ["stop"] = stopUnix,
        };

        if (0 < streams.Length)
        {
            file["info"] = streams.Cast<object>().ToList();
        }

        return new List<object> { file };
    }

    /// <summary>One entry of the per file stream list.</summary>
    public static Dictionary<string, object> RecordedStream(
        string type, string? language = null, int? width = null, int? height = null)
    {
        Dictionary<string, object> stream = new() { ["type"] = type };

        if (language is not null)
        {
            stream["language"] = language;
        }

        if (width.HasValue)
        {
            stream["width"] = width.Value;
        }

        if (height.HasValue)
        {
            stream["height"] = height.Value;
        }

        return stream;
    }

    private static long ToUnix(DateTime utc) =>
        (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
}
