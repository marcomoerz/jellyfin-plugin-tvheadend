using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.DataHelper;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers the details TVHeadend attaches to a finished recording: which streams it contains and
/// how large it is.
/// </summary>
/// <remarks>
/// Both feed Jellyfin's playback decision. Without codecs it cannot direct play, and without a
/// bitrate it assumes 40 Mbit/s and exceeds every client limit — either one forces a transcode.
/// </remarks>
public class RecordedFileDetailsTests
{
    private const long Start = 1_788_000_000L;

    private static DvrDataHelper CreateHelper() => new(NullLogger<DvrDataHelper>.Instance);

    private static async Task<MyRecordingInfo> SingleRecording(DvrDataHelper helper) =>
        Assert.Single(await helper.buildDvrInfos(CancellationToken.None));

    [Fact]
    public async Task RecordedStreams_BecomeMediaStreamsWithCodecs()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "completed", files:
            HtspMessageFactory.RecordedFiles(
                sizeBytes: 1_000_000, startUnix: Start, stopUnix: Start + 100,
                HtspMessageFactory.RecordedStream("H264", width: 1920, height: 1080),
                HtspMessageFactory.RecordedStream("AC3", language: "ger"))));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Equal(2, recording.MediaStreams.Count);

        MediaStream video = recording.MediaStreams[0];
        Assert.Equal(MediaStreamType.Video, video.Type);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(1920, video.Width);

        MediaStream audio = recording.MediaStreams[1];
        Assert.Equal(MediaStreamType.Audio, audio.Type);
        Assert.Equal("ac3", audio.Codec);
        Assert.Equal("ger", audio.Language);
    }

    /// <summary>One megabyte over a hundred seconds is eighty kilobits per second.</summary>
    [Fact]
    public async Task Bitrate_IsDerivedFromSizeAndDuration()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "completed", files:
            HtspMessageFactory.RecordedFiles(
                sizeBytes: 1_000_000, startUnix: Start, stopUnix: Start + 100)));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Equal(80_000, recording.Bitrate);
    }

    [Theory]
    [InlineData(0, 100)]      // no size
    [InlineData(1_000_000, 0)] // no duration
    public async Task Bitrate_IsAbsentWhenTheNumbersAreUnusable(long sizeBytes, long durationSeconds)
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "completed", files:
            HtspMessageFactory.RecordedFiles(sizeBytes, Start, Start + durationSeconds)));

        Assert.Null((await SingleRecording(helper)).Bitrate);
    }

    /// <summary>
    /// An interrupted recording is split across files. The bitrate has to average over all of
    /// them rather than describe only the first.
    /// </summary>
    [Fact]
    public async Task Bitrate_AveragesOverEveryFile()
    {
        List<object> files = HtspMessageFactory.RecordedFiles(1_000_000, Start, Start + 100);
        files.Add(new Dictionary<string, object>
        {
            ["filename"] = "/recordings/example-2.ts",
            ["size"] = 3_000_000L,
            ["start"] = Start + 200,
            ["stop"] = Start + 500,
        });

        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "completed", files: files));

        // 4 MB over 400 s.
        Assert.Equal(80_000, (await SingleRecording(helper)).Bitrate);
    }

    /// <summary>
    /// Recordings made by an older TVHeadend carry no file details at all, and must still show up.
    /// </summary>
    [Fact]
    public async Task RecordingWithoutFileDetails_StillWorks()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "completed"));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Empty(recording.MediaStreams);
        Assert.Null(recording.Bitrate);
    }

    [Fact]
    public async Task FileWithoutStreamInfo_LeavesTheStreamsEmptyButKeepsTheBitrate()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "completed", files:
            HtspMessageFactory.RecordedFiles(1_000_000, Start, Start + 100)));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Empty(recording.MediaStreams);
        Assert.Equal(80_000, recording.Bitrate);
    }

    /// <summary>
    /// Signalling components sit in the same list as the media ones and must not be described as
    /// streams Jellyfin should try to play.
    /// </summary>
    [Fact]
    public async Task SignallingComponents_AreLeftOut()
    {
        DvrDataHelper helper = CreateHelper();
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(1, "completed", files:
            HtspMessageFactory.RecordedFiles(
                sizeBytes: 1_000_000, startUnix: Start, stopUnix: Start + 100,
                HtspMessageFactory.RecordedStream("H264"),
                HtspMessageFactory.RecordedStream("PCR"),
                HtspMessageFactory.RecordedStream("CA"),
                HtspMessageFactory.RecordedStream("AAC"))));

        MyRecordingInfo recording = await SingleRecording(helper);

        Assert.Equal(2, recording.MediaStreams.Count);
        Assert.Equal(new[] { "h264", "aac" }, recording.MediaStreams.Select(stream => stream.Codec));
    }
}
