using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.DataHelper;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers watching a recording that is still being written.
/// </summary>
/// <remarks>
/// TVHeadend only writes the per file stop time when the muxer closes, and the stop time on the
/// entry is the scheduled end — in the future, and movable while the recording runs. Only the
/// elapsed time describes what a viewer can actually watch, so that is what has to be reported.
/// </remarks>
public class RunningRecordingTests
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RecordingStart = new(2026, 9, 5, 20, 0, 0, DateTimeKind.Utc);

    private static long ToUnix(DateTime utc) => (long)(utc - UnixEpoch).TotalSeconds;

    private static DvrDataHelper HelperAt(DateTime now) =>
        new(NullLogger<DvrDataHelper>.Instance, () => now);

    /// <summary>Builds a running recording: file started, no stop yet, live dataSize.</summary>
    private static HTSP.HTSMessage RunningEntry(
        DateTime scheduledStop, long dataSizeBytes, params Dictionary<string, object>[] streams)
    {
        Dictionary<string, object> file = new()
        {
            ["filename"] = "/recordings/running.ts",
            ["start"] = ToUnix(RecordingStart),
        };

        if (0 < streams.Length)
        {
            file["info"] = streams.Cast<object>().ToList();
        }

        HTSP.HTSMessage entry = new();
        entry.Method = "dvrEntryAdd";
        entry.putField("id", 1);
        entry.putField("channel", 1);
        entry.putField("state", "recording");
        entry.putField("title", "Running Recording");
        entry.putField("start", ToUnix(RecordingStart));
        entry.putField("stop", ToUnix(scheduledStop));
        entry.putField("dataSize", dataSizeBytes);
        entry.putField("files", new List<object> { file });

        return HtspMessageFactory.Wire(entry);
    }

    private static async Task<MyRecordingInfo> SingleRecording(DvrDataHelper helper) =>
        Assert.Single(await helper.buildDvrInfos(CancellationToken.None));

    [Fact]
    public async Task RunningRecording_IsOfferedForPlayback()
    {
        DvrDataHelper helper = HelperAt(RecordingStart.AddMinutes(10));
        helper.dvrEntryAdd(RunningEntry(RecordingStart.AddMinutes(30), 100_000_000));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Equal(RecordingStatus.InProgress, recording.Status);
    }

    /// <summary>
    /// Ten minutes in, ten minutes are watchable — not the thirty that were scheduled.
    /// </summary>
    [Fact]
    public async Task RecordedDuration_IsWhatHasBeenWrittenSoFar()
    {
        DvrDataHelper helper = HelperAt(RecordingStart.AddMinutes(10));
        helper.dvrEntryAdd(RunningEntry(RecordingStart.AddMinutes(30), 100_000_000));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Equal(TimeSpan.FromMinutes(10), recording.RecordedDuration);
    }

    /// <summary>
    /// The scheduled stop is a moving target: extending the recording must extend what is
    /// offered, and it must never promise more than has been written.
    /// </summary>
    [Fact]
    public async Task ExtendingTheStopTime_DoesNotChangeWhatIsAvailableYet()
    {
        DvrDataHelper helper = HelperAt(RecordingStart.AddMinutes(10));
        helper.dvrEntryAdd(RunningEntry(RecordingStart.AddMinutes(30), 100_000_000));

        // The user pushes the end out by another hour.
        HTSP.HTSMessage update = new();
        update.Method = "dvrEntryUpdate";
        update.putField("id", 1);
        update.putField("stop", ToUnix(RecordingStart.AddMinutes(90)));
        helper.dvrEntryUpdate(HtspMessageFactory.Wire(update));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Equal(TimeSpan.FromMinutes(10), recording.RecordedDuration);
    }

    /// <summary>As time passes, more of the recording becomes watchable.</summary>
    [Fact]
    public async Task RecordedDuration_GrowsWithTheClock()
    {
        HTSP.HTSMessage entry = RunningEntry(RecordingStart.AddMinutes(30), 100_000_000);

        DvrDataHelper early = HelperAt(RecordingStart.AddMinutes(5));
        early.dvrEntryAdd(entry);

        DvrDataHelper later = HelperAt(RecordingStart.AddMinutes(20));
        later.dvrEntryAdd(entry);

        Assert.Equal(TimeSpan.FromMinutes(5), (await SingleRecording(early)).RecordedDuration);
        Assert.Equal(TimeSpan.FromMinutes(20), (await SingleRecording(later)).RecordedDuration);
    }

    /// <summary>
    /// A clock running ahead of TVHeadend must not invent content beyond the scheduled end.
    /// </summary>
    [Fact]
    public async Task DurationIsCappedAtTheScheduledEnd()
    {
        DvrDataHelper helper = HelperAt(RecordingStart.AddMinutes(45));
        helper.dvrEntryAdd(RunningEntry(RecordingStart.AddMinutes(30), 100_000_000));

        Assert.Equal(TimeSpan.FromMinutes(30), (await SingleRecording(helper)).RecordedDuration);
    }

    [Fact]
    public async Task NothingRecordedYet_ReportsNoDuration()
    {
        DvrDataHelper helper = HelperAt(RecordingStart);
        helper.dvrEntryAdd(RunningEntry(RecordingStart.AddMinutes(30), 0));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Null(recording.RecordedDuration);
        Assert.Null(recording.Bitrate);
    }

    /// <summary>
    /// A running recording has no per file stop time, so its bitrate has to come from the live
    /// dataSize instead. 75 MB over 600 s is one megabit per second.
    /// </summary>
    [Fact]
    public async Task Bitrate_ComesFromTheLiveFileSize()
    {
        DvrDataHelper helper = HelperAt(RecordingStart.AddMinutes(10));
        helper.dvrEntryAdd(RunningEntry(RecordingStart.AddMinutes(30), 75_000_000));

        Assert.Equal(1_000_000, (await SingleRecording(helper)).Bitrate);
    }

    /// <summary>
    /// TVHeadend writes the stream list when the recording starts, so a running recording can be
    /// direct played just like a finished one.
    /// </summary>
    [Fact]
    public async Task RunningRecording_AlreadyKnowsItsCodecs()
    {
        DvrDataHelper helper = HelperAt(RecordingStart.AddMinutes(10));
        helper.dvrEntryAdd(RunningEntry(
            RecordingStart.AddMinutes(30),
            100_000_000,
            HtspMessageFactory.RecordedStream("H264", width: 1280, height: 720),
            HtspMessageFactory.RecordedStream("AAC", language: "eng")));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Equal(new[] { "h264", "aac" }, recording.MediaStreams.Select(stream => stream.Codec));
    }

    /// <summary>
    /// Once the muxer closes, the file carries its own stop time and the clock stops mattering.
    /// </summary>
    [Fact]
    public async Task WhenTheRecordingFinishes_TheFileTimesTakeOver()
    {
        DvrDataHelper helper = HelperAt(RecordingStart.AddHours(5));
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "completed", files:
            HtspMessageFactory.RecordedFiles(
                sizeBytes: 75_000_000,
                startUnix: ToUnix(RecordingStart),
                stopUnix: ToUnix(RecordingStart.AddMinutes(10)))));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Equal(TimeSpan.FromMinutes(10), recording.RecordedDuration);
        Assert.Equal(1_000_000, recording.Bitrate);
    }
}
