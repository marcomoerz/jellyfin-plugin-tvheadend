using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.DataHelper;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers the mapping from TVHeadend DVR entries to the timer and recording types Jellyfin
/// consumes. This is where the recording indicator and the delete behaviour are decided.
/// </summary>
public class DvrDataHelperTests
{
    private static DvrDataHelper CreateHelper() => new(NullLogger<DvrDataHelper>.Instance);

    private static async Task<IReadOnlyList<TimerInfo>> TimersOf(DvrDataHelper helper) =>
        (await helper.buildPendingTimersInfos(CancellationToken.None)).ToList();

    private static async Task<IReadOnlyList<MyRecordingInfo>> RecordingsOf(DvrDataHelper helper) =>
        (await helper.buildDvrInfos(CancellationToken.None)).ToList();

    /// <summary>
    /// A running recording has to stay in the timer list. Jellyfin marks a program as being
    /// recorded by matching a timer against it, so dropping the entry the moment recording starts
    /// removes the indicator exactly when it matters.
    /// </summary>
    [Fact]
    public async Task PendingTimers_IncludeRunningRecordings()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "scheduled"));
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(2, "recording"));

        IReadOnlyList<TimerInfo> timers = await TimersOf(helper);

        Assert.Equal(2, timers.Count);
        Assert.Equal(RecordingStatus.New, timers.Single(t => t.Id == "1").Status);
        Assert.Equal(RecordingStatus.InProgress, timers.Single(t => t.Id == "2").Status);
    }

    [Fact]
    public async Task PendingTimers_ExcludeFinishedEntries()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "completed"));
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(2, "missed"));
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(3, "invalid"));

        Assert.Empty(await TimersOf(helper));
    }

    /// <summary>
    /// The link back to the EPG event. Without it Jellyfin cannot pair the timer with the
    /// program, and no recording indicator appears.
    /// </summary>
    [Fact]
    public async Task PendingTimers_CarryTheEpgEventId()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "recording", eventId: 987654));

        TimerInfo timer = Assert.Single(await TimersOf(helper));

        Assert.Equal("987654", timer.ProgramId);
    }

    [Fact]
    public async Task PendingTimers_WithoutEventId_HaveNoProgramId()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "scheduled"));

        TimerInfo timer = Assert.Single(await TimersOf(helper));

        Assert.True(string.IsNullOrEmpty(timer.ProgramId));
    }

    [Fact]
    public async Task PendingTimers_CarryTheSeriesTimerId()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "scheduled", autorecId: "abc123"));

        TimerInfo timer = Assert.Single(await TimersOf(helper));

        Assert.Equal("abc123", timer.SeriesTimerId);
    }

    [Theory]
    [InlineData("completed", RecordingStatus.Completed)]
    [InlineData("recording", RecordingStatus.InProgress)]
    [InlineData("missed", RecordingStatus.Error)]
    public async Task Recordings_MapTheirState(string state, RecordingStatus expected)
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, state));

        MyRecordingInfo recording = Assert.Single(await RecordingsOf(helper));

        Assert.Equal(expected, recording.Status);
    }

    [Fact]
    public async Task Recordings_ExcludeScheduledEntries()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "scheduled"));

        Assert.Empty(await RecordingsOf(helper));
    }

    /// <summary>
    /// TVHeadend keeps deleted recordings around with state "completed" and an error saying the
    /// file is gone. Showing those produces tiles that cannot be played.
    /// </summary>
    [Fact]
    public async Task Recordings_SkipEntriesWhoseFileIsMissing()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "completed", error: "File missing"));
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(2, "completed"));

        MyRecordingInfo recording = Assert.Single(await RecordingsOf(helper));

        Assert.Equal("2", recording.Id);
    }

    [Fact]
    public async Task Recordings_CarryTitleAndEpisodeTitle()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(
            1, "completed", title: "American Dad!", subtitle: "Pilot", description: "First episode"));

        MyRecordingInfo recording = Assert.Single(await RecordingsOf(helper));

        Assert.Equal("American Dad!", recording.Name);
        Assert.Equal("Pilot", recording.EpisodeTitle);
        Assert.Equal("First episode", recording.Overview);
    }

    [Fact]
    public async Task EntryUpdate_MergesIntoTheStoredEntry()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "scheduled", title: "Before"));

        // TVHeadend sends partial updates: only the changed fields.
        HTSP.HTSMessage update = new HTSP.HTSMessage();
        update.Method = "dvrEntryUpdate";
        update.putField("id", 1);
        update.putField("state", "recording");
        update.putField("title", "After");
        helper.dvrEntryUpdate(HtspMessageFactory.Wire(update));

        TimerInfo timer = Assert.Single(await TimersOf(helper));

        Assert.Equal(RecordingStatus.InProgress, timer.Status);
        Assert.Equal("After", timer.Name);
    }

    [Fact]
    public async Task EntryDelete_RemovesTheEntry()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "scheduled"));

        HTSP.HTSMessage delete = new HTSP.HTSMessage();
        delete.Method = "dvrEntryDelete";
        delete.putField("id", 1);
        helper.dvrEntryDelete(HtspMessageFactory.Wire(delete));

        Assert.Empty(await TimersOf(helper));
    }

    /// <summary>
    /// Clean runs on every reconnect. Leaving stale entries behind is what kept deleted
    /// recordings visible until the server was restarted.
    /// </summary>
    [Fact]
    public async Task Clean_DropsEverything()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "scheduled"));
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(2, "completed"));

        helper.clean();

        Assert.Empty(await TimersOf(helper));
        Assert.Empty(await RecordingsOf(helper));
    }

    [Fact]
    public async Task DuplicateAdd_DoesNotDuplicateTheEntry()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "scheduled"));
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "scheduled"));

        Assert.Single(await TimersOf(helper));
    }
}
