using System.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.Tests;

/// <summary>
/// The HTSP wire format. Everything the plugin knows about TVHeadend arrives through here, so a
/// defect in this layer shows up as unexplained missing data much further up.
/// </summary>
public class HtsMessageTests
{
    private static HTSMessage RoundTrip(HTSMessage message) =>
        HTSMessage.parse(message.BuildBytes(), NullLogger<HTSMessage>.Instance);

    [Fact]
    public void Strings_SurviveTheRoundTrip()
    {
        HTSMessage message = new HTSMessage();
        message.Method = "channelAdd";
        message.putField("channelName", "Das Erste HD");

        HTSMessage parsed = RoundTrip(message);

        Assert.Equal("channelAdd", parsed.Method);
        Assert.Equal("Das Erste HD", parsed.getString("channelName"));
    }

    [Fact]
    public void NonAsciiStrings_SurviveTheRoundTrip()
    {
        HTSMessage message = new HTSMessage();
        message.putField("title", "Tatort: Väter und Söhne");

        Assert.Equal("Tatort: Väter und Söhne", RoundTrip(message).getString("title"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(256)]
    [InlineData(65537)]      // 0x00010001 - a zero byte between two significant ones
    [InlineData(16777217)]   // 0x01000001 - two of them
    [InlineData(-256)]
    public void Integers_SurviveTheRoundTrip(int value)
    {
        HTSMessage message = new HTSMessage();
        message.putField("value", value);

        Assert.Equal(value, RoundTrip(message).getInt("value"));
    }

    [Fact]
    public void UnixTimestamps_SurviveTheRoundTrip()
    {
        // Recording start and stop travel as 64 bit seconds.
        const long Timestamp = 1_788_000_000L;

        HTSMessage message = new HTSMessage();
        message.putField("start", Timestamp);

        Assert.Equal(Timestamp, RoundTrip(message).getLong("start"));
    }

    [Fact]
    public void ByteArrays_SurviveTheRoundTrip()
    {
        // The authentication challenge is binary.
        byte[] challenge = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        HTSMessage message = new HTSMessage();
        message.putField("challenge", challenge);

        Assert.Equal(challenge, RoundTrip(message).getByteArray("challenge"));
    }

    [Fact]
    public void NestedListsOfMaps_SurviveTheRoundTrip()
    {
        // This is the shape of the "services" list on a channel and of "events" on an EPG reply.
        List<object> services = new()
        {
            new Dictionary<string, object> { ["type"] = "HDTV", ["name"] = "first" },
            new Dictionary<string, object> { ["type"] = "SDTV", ["name"] = "second" },
        };

        HTSMessage message = new HTSMessage();
        message.putField("services", services);

        IList parsed = RoundTrip(message).getList("services");

        Assert.Equal(2, parsed.Count);
        Assert.Equal("HDTV", ((HTSMessage)parsed[0]!).getString("type"));
        Assert.Equal("second", ((HTSMessage)parsed[1]!).getString("name"));
    }

    [Fact]
    public void MissingField_FallsBackToTheDefault()
    {
        HTSMessage message = RoundTrip(new HTSMessage());

        Assert.False(message.containsField("nothing"));
        Assert.Equal(7, message.getInt("nothing", 7));
        Assert.Equal("fallback", message.getString("nothing", "fallback"));
    }

    [Fact]
    public void Frame_StartsWithABigEndianLengthPrefix()
    {
        HTSMessage message = new HTSMessage();
        message.Method = "hello";

        byte[] frame = message.BuildBytes();
        long announced = HTSMessage.uIntToLong(frame[0], frame[1], frame[2], frame[3]);

        Assert.Equal(frame.Length - 4, announced);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0L)]
    [InlineData(0, 0, 0, 1, 1L)]
    [InlineData(0, 0, 1, 0, 256L)]
    [InlineData(255, 255, 255, 255, 4294967295L)]
    public void LengthPrefix_IsDecodedAsUnsigned(byte b1, byte b2, byte b3, byte b4, long expected)
    {
        Assert.Equal(expected, HTSMessage.uIntToLong(b1, b2, b3, b4));
    }

    /// <summary>
    /// A parsed message caches the bytes it came from. Modifying it has to invalidate that cache,
    /// otherwise a forwarded message would silently carry its original content.
    /// </summary>
    [Fact]
    public void ModifyingAParsedMessage_RebuildsItsBytes()
    {
        HTSMessage original = new HTSMessage();
        original.Method = "test";
        original.putField("value", 1);

        HTSMessage parsed = RoundTrip(original);
        parsed.putField("value", 2);

        Assert.Equal(2, RoundTrip(parsed).getInt("value"));
    }

    [Fact]
    public void RemovingAFieldFromAParsedMessage_RebuildsItsBytes()
    {
        HTSMessage original = new HTSMessage();
        original.putField("value", 1);

        HTSMessage parsed = RoundTrip(original);
        parsed.removeField("value");

        Assert.False(RoundTrip(parsed).containsField("value"));
    }

    [Fact]
    public void TruncatedFrame_IsRejectedInsteadOfMisparsed()
    {
        HTSMessage message = new HTSMessage();
        message.putField("value", 1);

        byte[] frame = message.BuildBytes();
        byte[] truncated = frame.Take(frame.Length - 1).ToArray();

        Assert.Null(HTSMessage.parse(truncated, NullLogger<HTSMessage>.Instance));
    }

    [Fact]
    public void FrameShorterThanTheLengthPrefix_IsRejected()
    {
        Assert.Null(HTSMessage.parse(new byte[] { 0, 0 }, NullLogger<HTSMessage>.Instance));
    }
}
