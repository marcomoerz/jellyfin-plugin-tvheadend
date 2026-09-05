using MediaBrowser.Controller.LiveTv;
using TVHeadEnd.DataHelper;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers the recurring recording: a TVHeadend autorec entry, built from what Jellyfin calls a
/// series timer.
/// </summary>
public class AutorecRequestTests
{
    private static readonly TimeSpan Berlin = TimeSpan.FromHours(2);

    /// <summary>A rule for 20:15 Berlin time, one channel, weekdays only.</summary>
    private static SeriesTimerInfo Timer(Action<SeriesTimerInfo>? adjust = null)
    {
        SeriesTimerInfo timer = new()
        {
            Id = "auto-1",
            Name = "Das grosse Backen",
            ChannelId = "1234",
            // 20:15 in Berlin during summer time.
            StartDate = new DateTime(2026, 9, 5, 18, 15, 0, DateTimeKind.Utc),
            Days = [DayOfWeek.Monday, DayOfWeek.Wednesday],
            PrePaddingSeconds = 120,
            PostPaddingSeconds = 300,
        };

        adjust?.Invoke(timer);
        return timer;
    }

    /// <summary>Sends the message through the wire format, the way TVHeadend receives it.</summary>
    private static HTSMessage Sent(HTSMessage request) => HtspMessageFactory.Wire(request);

    private static HTSMessage Created(Action<SeriesTimerInfo>? adjust = null, TimeSpan? offset = null) =>
        Sent(AutorecRequest.Create(Timer(adjust), priority: 3, profileName: "hd", offset ?? Berlin));

    [Fact]
    public void Create_AsksTVHeadendToAddAnAutorecEntry()
    {
        HTSMessage request = Created();

        Assert.Equal("addAutorecEntry", request.Method);
        Assert.Equal(3, request.getInt("priority"));
        Assert.Equal("hd", request.getString("configName"));
        Assert.Equal(1234, request.getInt("channelId"));
    }

    [Fact]
    public void Update_NamesTheEntryItChanges()
    {
        HTSMessage request = Sent(AutorecRequest.Update(Timer(), 3, "hd", Berlin));

        Assert.Equal("updateAutorecEntry", request.Method);
        Assert.Equal("auto-1", request.getString("id"));
    }

    /// <summary>TVHeadend counts padding in minutes, Jellyfin in seconds.</summary>
    [Fact]
    public void Padding_IsSentInMinutes()
    {
        HTSMessage request = Created();

        Assert.Equal(2, request.getLong("startExtra"));
        Assert.Equal(5, request.getLong("stopExtra"));
    }

    /// <summary>
    /// The window is centred on the start time, in the server's own time zone. 20:15 Berlin is
    /// 1215 minutes past midnight, so the window runs from 1200 to 1230.
    /// </summary>
    [Fact]
    public void StartWindow_IsCentredOnTheServerLocalStartTime()
    {
        HTSMessage request = Created();

        Assert.Equal(20 * 60, request.getInt("start"));
        Assert.Equal(20 * 60 + 30, request.getInt("startWindow"));
    }

    /// <summary>
    /// The same instant on a server running UTC is two hours earlier in the day. Ignoring the
    /// offset would put the rule at 18:00 and it would never fire.
    /// </summary>
    [Fact]
    public void StartWindow_FollowsTheServerTimeZone()
    {
        HTSMessage request = Created(offset: TimeSpan.Zero);

        Assert.Equal(18 * 60, request.getInt("start"));
    }

    /// <summary>A window straddling midnight has to wrap, not go negative.</summary>
    [Fact]
    public void StartWindow_WrapsAroundMidnight()
    {
        HTSMessage request = Created(
            timer => timer.StartDate = new DateTime(2026, 9, 5, 22, 10, 0, DateTimeKind.Utc),
            offset: Berlin);

        // 00:10 local, so the window opens at 23:55 the previous day and closes at 00:25.
        Assert.Equal(23 * 60 + 55, request.getInt("start"));
        Assert.Equal(25, request.getInt("startWindow"));
    }

    [Fact]
    public void RecordAnyTime_LeavesTheWindowOpen()
    {
        HTSMessage request = Created(timer => timer.RecordAnyTime = true);

        Assert.Equal(-1, request.getInt("start"));
        Assert.Equal(-1, request.getInt("startWindow"));
    }

    /// <summary>
    /// At the protocol version this plugin negotiates there is no value meaning "any channel",
    /// so the field has to be absent.
    /// </summary>
    [Fact]
    public void RecordAnyChannel_LeavesOutTheChannel()
    {
        Assert.False(Created(timer => timer.RecordAnyChannel = true).containsField("channelId"));
    }

    [Fact]
    public void SelectedDays_BecomeTheDayMask()
    {
        // Monday is bit 0, Wednesday is bit 2.
        Assert.Equal(0x05, Created().getInt("daysOfWeek"));
    }

    /// <summary>An absent mask is how TVHeadend is told that every day counts.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void EveryDay_LeavesOutTheDayMask(int dayCount)
    {
        HTSMessage request = Created(
            timer => timer.Days = Enum.GetValues<DayOfWeek>().Take(dayCount).ToList());

        Assert.False(request.containsField("daysOfWeek"));
    }

    [Fact]
    public void RecordNewOnly_AsksForUniqueProgrammes()
    {
        Assert.Equal(0, Created().getInt("dupDetect"));
        Assert.Equal(14, Created(timer => timer.RecordNewOnly = true).getInt("dupDetect"));
    }

    /// <summary>
    /// TVHeadend reads the title as a case insensitive regular expression, so a title carrying
    /// punctuation would match the wrong programmes or none.
    /// </summary>
    [Theory]
    [InlineData("Wetten, dass..?", @"Wetten, dass\.\.\?")]
    [InlineData("Tatort (Wiederholung)", @"Tatort \(Wiederholung\)")]
    [InlineData("Was bin ich? [HD]", @"Was bin ich\? \[HD\]")]
    [InlineData("Zack + Zack", @"Zack \+ Zack")]
    [InlineData("heute journal", "heute journal")]
    public void Title_IsEscapedIntoALiteralPattern(string name, string expected)
    {
        Assert.Equal(expected, Created(timer => timer.Name = name).getString("title"));
    }

    /// <summary>Spaces stay as they are: not every regex engine accepts them escaped.</summary>
    [Fact]
    public void Title_KeepsItsSpacesUnescaped()
    {
        Assert.DoesNotContain(@"\ ", Created().getString("title"));
    }
}
