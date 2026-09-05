using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using TVHeadEnd;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers remembering what a live channel contains, so it is probed once rather than before
/// every playback.
/// </summary>
public class ChannelStreamProfileCacheTests
{
    private static readonly DateTime Noon = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private static MediaSourceInfo Probed(params MediaStream[] streams)
    {
        return new MediaSourceInfo
        {
            Container = "mpegts",
            Bitrate = 4_000_000,
            MediaStreams = streams.ToList(),
        };
    }

    private static MediaStream Video(string codec = "h264") =>
        new() { Type = MediaStreamType.Video, Codec = codec, Index = 0, Width = 1920, Height = 1080 };

    private static MediaStream Audio(string codec = "ac3") =>
        new() { Type = MediaStreamType.Audio, Codec = codec, Index = 1, Channels = 6 };

    [Fact]
    public void UnknownChannel_HasToBeProbed()
    {
        ChannelStreamProfileCache cache = new();

        Assert.Null(cache.Get("channel-1"));
    }

    [Fact]
    public void AfterProbing_TheChannelIsKnown()
    {
        ChannelStreamProfileCache cache = new();

        Assert.True(cache.Remember("channel-1", Probed(Video(), Audio())));

        ChannelStreamProfile? profile = cache.Get("channel-1");
        Assert.NotNull(profile);
        Assert.Equal(new[] { "h264", "ac3" }, profile.Streams.Select(stream => stream.Codec));
        Assert.Equal("mpegts", profile.Container);
        Assert.Equal(4_000_000, profile.Bitrate);
    }

    [Fact]
    public void ChannelsAreKeptApart()
    {
        ChannelStreamProfileCache cache = new();
        cache.Remember("channel-1", Probed(Video("h264")));
        cache.Remember("channel-2", Probed(Video("mpeg2video")));

        Assert.Equal("h264", cache.Get("channel-1")!.Streams[0].Codec);
        Assert.Equal("mpeg2video", cache.Get("channel-2")!.Streams[0].Codec);
    }

    /// <summary>
    /// A probe that produced nothing usable must not be remembered, or it would suppress every
    /// later attempt to find out.
    /// </summary>
    [Fact]
    public void AFruitlessProbe_IsNotRemembered()
    {
        ChannelStreamProfileCache cache = new();

        Assert.False(cache.Remember("channel-1", Probed()));
        Assert.False(cache.Remember("channel-2", new MediaSourceInfo()));
        Assert.False(cache.Remember("channel-3", Probed(new MediaStream { Type = MediaStreamType.Video })));

        Assert.Null(cache.Get("channel-1"));
        Assert.Null(cache.Get("channel-2"));
        Assert.Null(cache.Get("channel-3"));
    }

    /// <summary>
    /// A broadcaster can change codecs, so a measurement must not be trusted forever.
    /// </summary>
    [Fact]
    public void KnowledgeExpires()
    {
        DateTime now = Noon;
        ChannelStreamProfileCache cache = new(TimeSpan.FromHours(12), () => now);
        cache.Remember("channel-1", Probed(Video()));

        now = Noon.AddHours(11);
        Assert.NotNull(cache.Get("channel-1"));

        now = Noon.AddHours(13);
        Assert.Null(cache.Get("channel-1"));
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        ChannelStreamProfileCache cache = new();
        cache.Remember("channel-1", Probed(Video()));

        cache.Clear();

        Assert.Null(cache.Get("channel-1"));
    }

    [Fact]
    public void ApplyTo_FillsInStreamsContainerAndBitrate()
    {
        ChannelStreamProfileCache cache = new();
        cache.Remember("channel-1", Probed(Video(), Audio()));

        MediaSourceInfo target = new() { Container = "unknown" };
        ChannelStreamProfileCache.ApplyTo(cache.Get("channel-1")!, target);

        Assert.Equal(new[] { "h264", "ac3" }, target.MediaStreams.Select(stream => stream.Codec));
        Assert.Equal("mpegts", target.Container);
        Assert.Equal(4_000_000, target.Bitrate);
    }

    /// <summary>
    /// Jellyfin mutates the streams it is handed — forcing deinterlacing, for instance — so each
    /// caller has to get its own copies.
    /// </summary>
    [Fact]
    public void ApplyTo_HandsOutCopies()
    {
        ChannelStreamProfileCache cache = new();
        cache.Remember("channel-1", Probed(Video()));

        MediaSourceInfo first = new();
        ChannelStreamProfileCache.ApplyTo(cache.Get("channel-1")!, first);
        first.MediaStreams[0].IsInterlaced = true;
        first.MediaStreams[0].Codec = "tampered";

        MediaSourceInfo second = new();
        ChannelStreamProfileCache.ApplyTo(cache.Get("channel-1")!, second);

        Assert.Equal("h264", second.MediaStreams[0].Codec);
        Assert.False(second.MediaStreams[0].IsInterlaced);
    }

    /// <summary>What the probe could not tell must not overwrite what the caller already set.</summary>
    [Fact]
    public void ApplyTo_KeepsTheExistingContainerWhenNoneWasLearned()
    {
        ChannelStreamProfileCache cache = new();
        cache.Remember("channel-1", new MediaSourceInfo { MediaStreams = new List<MediaStream> { Video() } });

        MediaSourceInfo target = new() { Container = "mpegts" };
        ChannelStreamProfileCache.ApplyTo(cache.Get("channel-1")!, target);

        Assert.Equal("mpegts", target.Container);
        Assert.Null(target.Bitrate);
    }

    [Fact]
    public void MissingChannelId_IsHandled()
    {
        ChannelStreamProfileCache cache = new();

        Assert.False(cache.Remember(string.Empty, Probed(Video())));
        Assert.Null(cache.Get(string.Empty));
    }
}
