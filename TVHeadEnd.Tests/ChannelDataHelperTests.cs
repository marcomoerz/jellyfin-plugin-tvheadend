using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers how TVHeadend channels become Jellyfin channels, including the filters that decide
/// which ones never show up at all.
/// </summary>
public class ChannelDataHelperTests
{
    private static ChannelDataHelper CreateHelper() => new(NullLogger<ChannelDataHelper>.Instance);

    private static async Task<IReadOnlyList<ChannelInfo>> ChannelsOf(ChannelDataHelper helper) =>
        (await helper.BuildChannelInfos(CancellationToken.None)).ToList();

    private static HTSMessage Channel(
        int channelId,
        string name = "Das Erste HD",
        int number = 1,
        string serviceType = "HDTV",
        string? icon = null,
        int? minorNumber = null)
    {
        HTSMessage message = new HTSMessage();
        message.Method = "channelAdd";
        message.putField("channelId", channelId);
        message.putField("channelName", name);
        message.putField("channelNumber", number);
        message.putField("services", new List<object>
        {
            new Dictionary<string, object> { ["type"] = serviceType, ["name"] = "tuner" },
        });

        if (icon is not null)
        {
            message.putField("channelIcon", icon);
        }

        if (minorNumber.HasValue)
        {
            message.putField("channelNumberMinor", minorNumber.Value);
        }

        return HtspMessageFactory.Wire(message);
    }

    [Theory]
    [InlineData("HDTV", ChannelType.TV)]
    [InlineData("SDTV", ChannelType.TV)]
    [InlineData("FHDTV", ChannelType.TV)]
    [InlineData("UHDTV", ChannelType.TV)]
    [InlineData("Radio", ChannelType.Radio)]
    public void ServiceType_DecidesTheChannelType(string serviceType, ChannelType expected)
    {
        ChannelDataHelper helper = CreateHelper();
        helper.Add(Channel(1, serviceType: serviceType));

        ChannelInfo channel = ChannelsOf(helper).GetAwaiter().GetResult().Single();

        Assert.Equal(expected, channel.ChannelType);
    }

    [Theory]
    [InlineData("HDTV", true)]
    [InlineData("SDTV", false)]
    public async Task ServiceType_DecidesTheHdFlag(string serviceType, bool expected)
    {
        ChannelDataHelper helper = CreateHelper();
        helper.Add(Channel(1, serviceType: serviceType));

        ChannelInfo channel = Assert.Single(await ChannelsOf(helper));

        Assert.Equal(expected, channel.IsHD);
    }

    /// <summary>
    /// Channels of an unmapped service type are dropped. Worth pinning down, because a missing
    /// channel is otherwise very hard to explain.
    /// </summary>
    [Fact]
    public async Task UnknownServiceType_IsDropped()
    {
        ChannelDataHelper helper = CreateHelper();
        helper.Add(Channel(1, serviceType: "Other"));

        Assert.Empty(await ChannelsOf(helper));
    }

    /// <summary>TVHeadend uses number 0 for channels that are not meant to be listed.</summary>
    [Fact]
    public async Task ChannelWithoutANumber_IsNeverStored()
    {
        ChannelDataHelper helper = CreateHelper();
        helper.Add(Channel(1, number: 0));

        Assert.Empty(await ChannelsOf(helper));
    }

    [Fact]
    public async Task ChannelWithoutAName_IsDropped()
    {
        ChannelDataHelper helper = CreateHelper();
        helper.Add(Channel(1, name: string.Empty));

        Assert.Empty(await ChannelsOf(helper));
    }

    [Fact]
    public async Task MinorNumber_IsAppendedToTheChannelNumber()
    {
        ChannelDataHelper helper = CreateHelper();
        helper.Add(Channel(1, number: 3, minorNumber: 2));

        ChannelInfo channel = Assert.Single(await ChannelsOf(helper));

        Assert.Equal("3.2", channel.Number);
    }

    [Fact]
    public async Task AbsoluteIconUrl_IsUsedDirectly()
    {
        ChannelDataHelper helper = CreateHelper();
        helper.Add(Channel(1, icon: "http://tvh.example/icon.png"));

        ChannelInfo channel = Assert.Single(await ChannelsOf(helper));

        Assert.Equal("http://tvh.example/icon.png", channel.ImageUrl);
    }

    /// <summary>A relative icon path is remembered so the URL can be built with credentials later.</summary>
    [Fact]
    public async Task RelativeIconPath_IsRememberedAsAPicon()
    {
        ChannelDataHelper helper = CreateHelper();
        helper.Add(Channel(7, icon: "imagecache/42"));

        await ChannelsOf(helper);

        Assert.Equal("imagecache/42", helper.GetChannelIcon4ChannelId("7"));
    }

    [Fact]
    public void UnknownChannel_HasNoPicon()
    {
        ChannelDataHelper helper = CreateHelper();

        Assert.Null(helper.GetChannelIcon4ChannelId("does-not-exist"));
    }

    [Fact]
    public async Task ChannelUpdate_MergesIntoTheStoredChannel()
    {
        ChannelDataHelper helper = CreateHelper();
        helper.Add(Channel(1, name: "Before"));

        HTSMessage update = new HTSMessage();
        update.Method = "channelUpdate";
        update.putField("channelId", 1);
        update.putField("channelName", "After");
        helper.Add(HtspMessageFactory.Wire(update));

        ChannelInfo channel = Assert.Single(await ChannelsOf(helper));

        Assert.Equal("After", channel.Name);
    }

    /// <summary>Clean runs on reconnect; leftovers would describe a server we no longer follow.</summary>
    [Fact]
    public async Task Clean_DropsChannelsAndPicons()
    {
        ChannelDataHelper helper = CreateHelper();
        helper.Add(Channel(1, icon: "imagecache/1"));
        await ChannelsOf(helper);

        helper.Clean();

        Assert.Empty(await ChannelsOf(helper));
        Assert.Null(helper.GetChannelIcon4ChannelId("1"));
    }
}
