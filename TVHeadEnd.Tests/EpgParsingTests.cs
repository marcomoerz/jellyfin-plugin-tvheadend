using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers the EPG reply parser. It used to be a callback that filled itself in the background
/// while the caller polled a flag; it is now a pure function, which is what makes it testable.
/// </summary>
public class EpgParsingTests
{
    private static readonly DateTime WindowStart = new(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 9, 4, 22, 0, 0, DateTimeKind.Utc);

    private static long ToUnix(DateTime utc) =>
        (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    private static Dictionary<string, object> Event(
        long eventId,
        DateTime start,
        DateTime stop,
        string title = "Tagesschau",
        int channelId = 1)
    {
        return new Dictionary<string, object>
        {
            ["eventId"] = eventId,
            ["channelId"] = channelId,
            ["start"] = ToUnix(start),
            ["stop"] = ToUnix(stop),
            ["title"] = title,
        };
    }

    private static HTSMessage Reply(params Dictionary<string, object>[] events)
    {
        HTSMessage message = new HTSMessage();
        message.Method = "getEvents";
        message.putField("events", events.Cast<object>().ToList());
        return HtspMessageFactory.Wire(message);
    }

    private static IReadOnlyList<ProgramInfo> Parse(HTSMessage reply)
    {
        GetEventsResponseHandler parser = new(
            WindowStart, WindowEnd, NullLogger<LiveTvService>.Instance, CancellationToken.None);

        return parser.Parse(reply).ToList();
    }

    [Fact]
    public void EventInsideTheWindow_IsReturned()
    {
        IReadOnlyList<ProgramInfo> programs = Parse(Reply(
            Event(1, WindowStart.AddHours(1), WindowStart.AddHours(2), "Tatort")));

        ProgramInfo program = Assert.Single(programs);

        Assert.Equal("1", program.Id);
        Assert.Equal("Tatort", program.Name);
    }

    [Fact]
    public void EventStartingAfterTheWindow_IsSkipped()
    {
        IReadOnlyList<ProgramInfo> programs = Parse(Reply(
            Event(1, WindowEnd.AddHours(1), WindowEnd.AddHours(2))));

        Assert.Empty(programs);
    }

    [Fact]
    public void EventEndingBeforeTheWindow_IsSkipped()
    {
        IReadOnlyList<ProgramInfo> programs = Parse(Reply(
            Event(1, WindowStart.AddHours(-3), WindowStart.AddHours(-2))));

        Assert.Empty(programs);
    }

    [Fact]
    public void EventWithoutTimes_IsSkipped()
    {
        HTSMessage message = new HTSMessage();
        message.putField("events", new List<object>
        {
            new Dictionary<string, object> { ["eventId"] = 1L, ["title"] = "no times" },
        });

        Assert.Empty(Parse(HtspMessageFactory.Wire(message)));
    }

    [Fact]
    public void SeveralEvents_AreAllReturned()
    {
        IReadOnlyList<ProgramInfo> programs = Parse(Reply(
            Event(1, WindowStart.AddMinutes(10), WindowStart.AddMinutes(40), "First"),
            Event(2, WindowStart.AddMinutes(40), WindowStart.AddMinutes(70), "Second"),
            Event(3, WindowStart.AddMinutes(70), WindowStart.AddMinutes(100), "Third")));

        Assert.Equal(new[] { "First", "Second", "Third" }, programs.Select(p => p.Name));
    }

    [Fact]
    public void EventTimes_AreConvertedToUtc()
    {
        DateTime start = WindowStart.AddHours(1);
        DateTime stop = start.AddMinutes(45);

        ProgramInfo program = Assert.Single(Parse(Reply(Event(1, start, stop))));

        Assert.Equal(start, program.StartDate);
        Assert.Equal(stop, program.EndDate);
    }

    /// <summary>
    /// The program id has to be the TVHeadend event id: Jellyfin pairs a timer with a program by
    /// comparing it against the timer's ProgramId.
    /// </summary>
    [Fact]
    public void ProgramId_IsTheEventId()
    {
        ProgramInfo program = Assert.Single(Parse(Reply(
            Event(987654, WindowStart.AddHours(1), WindowStart.AddHours(2)))));

        Assert.Equal("987654", program.Id);
    }

    [Fact]
    public void ReplyWithoutEvents_YieldsNothing()
    {
        HTSMessage empty = HtspMessageFactory.Wire(new HTSMessage());

        Assert.Empty(Parse(empty));
    }
}
