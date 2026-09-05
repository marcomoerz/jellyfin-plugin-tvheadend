using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers reading a recurring recording back.
/// </summary>
/// <remarks>
/// Jellyfin edits a series timer by showing what it was told and sending the whole thing back.
/// So whatever the read side gets wrong, the next save writes into the rule. The read has to
/// describe the rule the same way the write does, or opening and saving an untouched rule
/// silently changes it.
/// </remarks>
public class AutorecRoundTripTests
{
    private static readonly TimeSpan Berlin = TimeSpan.FromHours(2);
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private static AutorecDataHelper Helper() =>
        new(NullLogger<AutorecDataHelper>.Instance, () => Now);

    /// <summary>Builds an autorec entry as TVHeadend announces it.</summary>
    private static HTSMessage Entry(Action<HTSMessage> adjust)
    {
        HTSMessage message = new();
        message.Method = "autorecEntryAdd";
        message.putField("id", "auto-1");
        message.putField("title", "Das grosse Backen");
        message.putField("daysOfWeek", 0x05);
        adjust(message);
        return HtspMessageFactory.Wire(message);
    }

    private static async Task<SeriesTimerInfo> Single(HTSMessage entry)
    {
        AutorecDataHelper helper = Helper();
        helper.autorecEntryAdd(entry);
        return Assert.Single(await helper.buildAutorecInfos(CancellationToken.None, Berlin));
    }

    /// <summary>
    /// TVHeadend stores where the window opens; the time the rule is about sits in its middle.
    /// </summary>
    [Fact]
    public async Task AWindowedRule_ReportsTheMiddleOfItsWindow()
    {
        SeriesTimerInfo timer = await Single(Entry(m =>
        {
            m.putField("start", 20 * 60);
            m.putField("startWindow", 20 * 60 + 30);
        }));

        Assert.False(timer.RecordAnyTime);
        // 20:15 in Berlin is 18:15 UTC.
        Assert.Equal(18, timer.StartDate.ToUniversalTime().Hour);
        Assert.Equal(15, timer.StartDate.ToUniversalTime().Minute);
    }

    /// <summary>A rule made elsewhere can carry any width, and it has to survive as it is.</summary>
    [Theory]
    [InlineData(20 * 60, 21 * 60, 20, 30)]           // an hour wide, centred on 20:30
    [InlineData(23 * 60 + 55, 25, 0, 10)]            // straddling midnight, centred on 00:10
    [InlineData(20 * 60, -1, 20, 0)]                 // no closing time: take the opening
    public async Task TheWidthOfTheWindow_IsMeasuredNotAssumed(
        int start, int end, int expectedHour, int expectedMinute)
    {
        SeriesTimerInfo timer = await Single(Entry(m =>
        {
            m.putField("start", start);
            m.putField("startWindow", end);
        }));

        DateTime serverLocal = timer.StartDate.ToUniversalTime() + Berlin;
        Assert.Equal(expectedHour, serverLocal.Hour);
        Assert.Equal(expectedMinute, serverLocal.Minute);
    }

    [Fact]
    public async Task ARuleWithoutAWindow_RecordsAtAnyTime()
    {
        Assert.True((await Single(Entry(m => m.putField("start", -1)))).RecordAnyTime);
        Assert.True((await Single(Entry(_ => { }))).RecordAnyTime);
    }

    [Fact]
    public async Task ARuleWithoutAChannel_RecordsOnAnyChannel()
    {
        Assert.True((await Single(Entry(_ => { }))).RecordAnyChannel);
        Assert.False((await Single(Entry(m => m.putField("channel", 1234)))).RecordAnyChannel);
    }

    [Fact]
    public async Task DuplicateDetection_BecomesRecordNewOnly()
    {
        Assert.False((await Single(Entry(m => m.putField("dupDetect", 0)))).RecordNewOnly);
        Assert.True((await Single(Entry(m => m.putField("dupDetect", 14)))).RecordNewOnly);
    }

    [Fact]
    public async Task TheDayMask_BecomesTheSelectedDays()
    {
        SeriesTimerInfo timer = await Single(Entry(_ => { }));

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], timer.Days);
    }

    /// <summary>
    /// The whole point: reading a rule and writing it straight back must not move it.
    /// </summary>
    [Fact]
    public async Task ReadingARuleAndSavingItUnchanged_LeavesItWhereItWas()
    {
        HTSMessage original = Entry(m =>
        {
            // The window TVHeadend stores: it opens at 20:00 and closes at 20:30, so the rule is
            // about 20:15.
            m.putField("start", 20 * 60);
            m.putField("startWindow", 20 * 60 + 30);
            m.putField("channel", 1234);
            m.putField("startExtra", 2L);
            m.putField("stopExtra", 5L);
        });

        SeriesTimerInfo asJellyfinSeesIt = await Single(original);
        HTSMessage rewritten = HtspMessageFactory.Wire(
            AutorecRequest.Update(asJellyfinSeesIt, priority: 3, profileName: "hd", Berlin));

        Assert.Equal(20 * 60, rewritten.getInt("start"));
        Assert.Equal(1234, rewritten.getInt("channelId"));
        Assert.Equal(0x05, rewritten.getInt("daysOfWeek"));
        Assert.Equal(2, rewritten.getLong("startExtra"));
        Assert.Equal(5, rewritten.getLong("stopExtra"));
    }
}
