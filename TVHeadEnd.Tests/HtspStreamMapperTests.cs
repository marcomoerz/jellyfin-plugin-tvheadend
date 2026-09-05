using MediaBrowser.Model.Entities;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers the translation of TVHeadend stream descriptions into Jellyfin media streams.
/// </summary>
/// <remarks>
/// The codec is the load bearing field: Jellyfin rejects direct play for a stream it cannot
/// name, so a wrong or missing mapping here shows up as unexplained transcoding.
/// </remarks>
public class HtspStreamMapperTests
{
    private static HTSMessage Stream(
        string type,
        int index = 1,
        string? language = null,
        int? width = null,
        int? height = null,
        int? channels = null,
        int? rate = null,
        int? duration = null,
        int? interlaced = null)
    {
        HTSMessage message = new HTSMessage();
        message.putField("type", type);
        message.putField("index", index);

        if (language is not null)
        {
            message.putField("language", language);
        }

        if (width.HasValue)
        {
            message.putField("width", width.Value);
        }

        if (height.HasValue)
        {
            message.putField("height", height.Value);
        }

        if (channels.HasValue)
        {
            message.putField("channels", channels.Value);
        }

        if (rate.HasValue)
        {
            message.putField("rate", rate.Value);
        }

        if (duration.HasValue)
        {
            message.putField("duration", duration.Value);
        }

        if (interlaced.HasValue)
        {
            message.putField("interlaced", interlaced.Value);
        }

        return HtspMessageFactory.Wire(message);
    }

    private static HTSMessage StreamWithoutIndex(string type)
    {
        HTSMessage message = new HTSMessage();
        message.putField("type", type);
        return HtspMessageFactory.Wire(message);
    }

    private static List<object> StreamList(params HTSMessage[] streams) =>
        streams.Cast<object>().ToList();

    [Theory]
    [InlineData("MPEG2VIDEO", "mpeg2video")]
    [InlineData("H264", "h264")]
    [InlineData("HEVC", "hevc")]
    [InlineData("VP8", "vp8")]
    [InlineData("VP9", "vp9")]
    [InlineData("MPEG2AUDIO", "mp2")]
    [InlineData("AC3", "ac3")]
    [InlineData("EAC3", "eac3")]
    [InlineData("AAC", "aac")]
    [InlineData("AAC-LATM", "aac")]
    [InlineData("AC-4", "ac4")]
    [InlineData("THEORA", "theora")]
    [InlineData("OPUS", "opus")]
    [InlineData("FLAC", "flac")]
    [InlineData("VORBIS", "vorbis")]
    public void KnownTypes_MapToTheirFfmpegName(string htspType, string expected)
    {
        Assert.Equal(expected, HtspStreamMapper.ToJellyfinCodec(htspType));
    }

    /// <summary>TVHeadend spells its types in upper case, but tolerating both costs nothing.</summary>
    [Theory]
    [InlineData("h264")]
    [InlineData("H264")]
    [InlineData("  H264  ")]
    public void TypeMatching_IgnoresCaseAndPadding(string htspType)
    {
        Assert.Equal("h264", HtspStreamMapper.ToJellyfinCodec(htspType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SOMETHING_NEW")]
    public void UnknownType_HasNoCodec(string? htspType)
    {
        Assert.Null(HtspStreamMapper.ToJellyfinCodec(htspType));
    }

    [Theory]
    [InlineData("H264", MediaStreamType.Video)]
    [InlineData("MPEG2VIDEO", MediaStreamType.Video)]
    [InlineData("AAC", MediaStreamType.Audio)]
    [InlineData("AC3", MediaStreamType.Audio)]
    [InlineData("DVBSUB", MediaStreamType.Subtitle)]
    [InlineData("TELETEXT", MediaStreamType.Subtitle)]
    public void TypeIsClassified(string htspType, MediaStreamType expected)
    {
        Assert.Equal(expected, HtspStreamMapper.ToStreamType(htspType));
    }

    [Fact]
    public void VideoStream_CarriesResolutionAndCodec()
    {
        MediaStream stream = Assert.Single(
            HtspStreamMapper.ToMediaStreams(StreamList(Stream("H264", width: 1920, height: 1080))));

        Assert.Equal(MediaStreamType.Video, stream.Type);
        Assert.Equal("h264", stream.Codec);
        Assert.Equal(1920, stream.Width);
        Assert.Equal(1080, stream.Height);
    }

    [Fact]
    public void AudioStream_CarriesChannelsAndSampleRate()
    {
        MediaStream stream = Assert.Single(
            HtspStreamMapper.ToMediaStreams(StreamList(Stream("AC3", channels: 6, rate: 48000))));

        Assert.Equal(MediaStreamType.Audio, stream.Type);
        Assert.Equal("ac3", stream.Codec);
        Assert.Equal(6, stream.Channels);
        Assert.Equal(48000, stream.SampleRate);
    }

    [Fact]
    public void Language_IsCarriedOver()
    {
        MediaStream stream = Assert.Single(
            HtspStreamMapper.ToMediaStreams(StreamList(Stream("AC3", language: "ger"))));

        Assert.Equal("ger", stream.Language);
    }

    /// <summary>Where an index is present TVHeadend counts from one, ffmpeg from zero.</summary>
    [Fact]
    public void Index_IsShiftedToTheFfmpegConvention()
    {
        IReadOnlyList<MediaStream> streams = HtspStreamMapper.ToMediaStreams(
            StreamList(Stream("H264", index: 1), Stream("AC3", index: 2)));

        Assert.Equal(0, streams[0].Index);
        Assert.Equal(1, streams[1].Index);
    }

    /// <summary>
    /// The per file info list of a DVR entry carries no index at all — TVHeadend builds those
    /// entries in dvr_rec.c without one — so the position has to stand in.
    /// </summary>
    [Fact]
    public void WithoutAnIndex_ThePositionIsUsed()
    {
        IReadOnlyList<MediaStream> streams = HtspStreamMapper.ToMediaStreams(
            StreamList(StreamWithoutIndex("H264"), StreamWithoutIndex("AC3"), StreamWithoutIndex("DVBSUB")));

        Assert.Equal(new[] { 0, 1, 2 }, streams.Select(stream => stream.Index));
    }

    /// <summary>Signalling components are not media and must not become streams.</summary>
    [Theory]
    [InlineData("PCR")]
    [InlineData("CA")]
    [InlineData("CAT")]
    [InlineData("RDS")]
    [InlineData("HBBTV")]
    [InlineData("MPEGTS")]
    [InlineData("UNKNOWN")]
    [InlineData("NONE")]
    [InlineData("INVALID")]
    public void SignallingComponents_AreNotStreams(string htspType)
    {
        Assert.Null(HtspStreamMapper.ToStreamType(htspType));
        Assert.Empty(HtspStreamMapper.ToMediaStreams(StreamList(Stream(htspType))));
    }

    /// <summary>
    /// The frame duration arrives in 90 kHz ticks. 3600 ticks is one fiftieth of a second.
    /// </summary>
    [Fact]
    public void FrameDuration_BecomesAFrameRate()
    {
        MediaStream stream = Assert.Single(
            HtspStreamMapper.ToMediaStreams(StreamList(Stream("H264", duration: 3600))));

        Assert.Equal(25f, stream.RealFrameRate);
    }

    [Fact]
    public void FrameRate_IsAbsentWhenTheDurationIsMissingOrNonsense()
    {
        Assert.Null(Assert.Single(HtspStreamMapper.ToMediaStreams(StreamList(Stream("H264")))).RealFrameRate);
        Assert.Null(Assert.Single(HtspStreamMapper.ToMediaStreams(StreamList(Stream("H264", duration: 0)))).RealFrameRate);
    }

    /// <summary>
    /// Claiming interlacing forces Jellyfin to deinterlace, which rules out direct play. It must
    /// only be reported when TVHeadend actually says so.
    /// </summary>
    [Fact]
    public void Interlacing_IsOnlyClaimedWhenReported()
    {
        Assert.False(Assert.Single(HtspStreamMapper.ToMediaStreams(StreamList(Stream("H264")))).IsInterlaced);
        Assert.False(Assert.Single(HtspStreamMapper.ToMediaStreams(StreamList(Stream("H264", interlaced: 0)))).IsInterlaced);
        Assert.True(Assert.Single(HtspStreamMapper.ToMediaStreams(StreamList(Stream("H264", interlaced: 1)))).IsInterlaced);
    }

    [Fact]
    public void MixedStreamList_IsFullyMapped()
    {
        IReadOnlyList<MediaStream> streams = HtspStreamMapper.ToMediaStreams(StreamList(
            Stream("H264", index: 1, width: 1280, height: 720),
            Stream("AAC", index: 2, channels: 2, language: "eng"),
            Stream("DVBSUB", index: 3, language: "ger")));

        Assert.Equal(3, streams.Count);
        Assert.Equal(MediaStreamType.Video, streams[0].Type);
        Assert.Equal(MediaStreamType.Audio, streams[1].Type);
        Assert.Equal(MediaStreamType.Subtitle, streams[2].Type);
    }

    /// <summary>
    /// A stream Jellyfin cannot name would be rejected for direct play anyway, so it is better
    /// left out than described as unknown.
    /// </summary>
    [Fact]
    public void UnrecognisedEntries_AreSkipped()
    {
        IReadOnlyList<MediaStream> streams = HtspStreamMapper.ToMediaStreams(StreamList(
            Stream("H264"), Stream("SOMETHING_NEW")));

        Assert.Single(streams);
        Assert.Equal("h264", streams[0].Codec);
    }

    [Fact]
    public void MissingOrEmptyList_YieldsNothing()
    {
        Assert.Empty(HtspStreamMapper.ToMediaStreams(null));
        Assert.Empty(HtspStreamMapper.ToMediaStreams(new List<object>()));
    }

    [Fact]
    public void EntriesThatAreNotMessages_AreIgnored()
    {
        Assert.Empty(HtspStreamMapper.ToMediaStreams(new List<object> { "not a message", 42 }));
    }
}
