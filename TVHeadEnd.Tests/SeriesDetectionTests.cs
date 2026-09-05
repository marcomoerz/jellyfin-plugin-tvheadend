using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers recognising that a programme belongs to a series.
/// </summary>
/// <remarks>
/// Jellyfin offers "record series" only for a programme whose <c>IsSeries</c> is set, so
/// everything the recurring recording can do hangs off this one flag. Which fields carry the
/// evidence depends entirely on the guide: some fill the episode title, some only number the
/// episodes, and some write all of it into the description and fill nothing.
/// </remarks>
public class SeriesDetectionTests
{
    private static readonly DateTime Start = new(2026, 9, 5, 19, 7, 0, DateTimeKind.Utc);

    private static long ToUnix(DateTime utc) =>
        (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    /// <summary>
    /// Builds a getEvents reply carrying one event, as it comes off the socket.
    /// </summary>
    /// <remarks>
    /// The event is a nested map, so it is written the way the wire format carries one.
    /// </remarks>
    private static HTSMessage Reply(Action<Dictionary<string, object>> describe)
    {
        Dictionary<string, object> evt = new()
        {
            ["eventId"] = 4711,
            ["channelId"] = 1,
            ["start"] = ToUnix(Start),
            ["stop"] = ToUnix(Start.AddMinutes(26)),
            ["title"] = "Brooklyn Nine-Nine",
        };

        describe(evt);

        HTSMessage response = new();
        response.Method = "getEvents";
        response.putField("events", new List<object> { evt });

        return HtspMessageFactory.Wire(response);
    }

    private static ProgramInfo Parse(Action<Dictionary<string, object>> describe)
    {
        GetEventsResponseHandler handler = new(
            Start.AddDays(-1), Start.AddDays(1), NullLogger<LiveTvService>.Instance, CancellationToken.None);

        return Assert.Single(handler.Parse(Reply(describe)));
    }

    /// <summary>An episode title is the clearest sign, and the one the plugin always used.</summary>
    [Fact]
    public void AnEpisodeTitle_MarksASeries()
    {
        ProgramInfo program = Parse(evt => evt["subtitle"] = "Wir waren heute alle Gina Linetti");

        Assert.True(program.IsSeries);
        Assert.Equal("Wir waren heute alle Gina Linetti", program.EpisodeTitle);
    }

    /// <summary>
    /// TVHeadend ties episodes together with a URI. The plugin used to read a numeric
    /// "serieslinkId", which TVHeadend does not have and never sent, so this was always empty.
    /// </summary>
    [Fact]
    public void ASeriesLink_MarksASeries()
    {
        ProgramInfo program = Parse(evt => evt["serieslinkUri"] = "ddprogid://brooklyn99");

        Assert.True(program.IsSeries);
        Assert.Equal("ddprogid://brooklyn99", program.SeriesId);
    }

    [Fact]
    public void EpisodeNumbering_MarksASeries()
    {
        ProgramInfo program = Parse(evt =>
        {
            evt["seasonNumber"] = 4;
            evt["episodeNumber"] = 3;
        });

        Assert.True(program.IsSeries);
        Assert.Equal(4, program.SeasonNumber);
        Assert.Equal(3, program.EpisodeNumber);
    }

    /// <summary>
    /// A guide that carries no separate numbers still writes the numbering as text.
    /// </summary>
    [Fact]
    public void OnScreenNumberingAlone_MarksASeries()
    {
        Assert.True(Parse(evt => evt["episodeOnscreen"] = "S4 E3").IsSeries);
    }

    /// <summary>
    /// The case that started this: ProSieben MAXX fills no episode field at all and writes the
    /// numbering into the text. TVHeadend has nothing structured to pass on, so the label has to
    /// be read back out or the series stays invisible.
    /// </summary>
    [Fact]
    public void ADescriptionThatCarriesEverything_IsReadBack()
    {
        ProgramInfo program = Parse(evt => evt["description"] =
            "S4 E3 - Wir waren heute alle Gina Linetti\nNoch immer befinden sich Jake und Holt "
            + "auf der Jagd nach Figgis.\n\nCredits: Andy Samberg\n\nCategories: sitcom");

        Assert.True(program.IsSeries);
        Assert.Equal(4, program.SeasonNumber);
        Assert.Equal(3, program.EpisodeNumber);
        Assert.Equal("Wir waren heute alle Gina Linetti", program.EpisodeTitle);

        // The label is taken off, or Jellyfin would print all of it twice.
        Assert.StartsWith("Noch immer", program.Overview);
    }

    /// <summary>What the guide itself said always wins; the text is only ever a fallback.</summary>
    [Fact]
    public void StructuredFields_BeatTheText()
    {
        ProgramInfo program = Parse(evt =>
        {
            evt["seasonNumber"] = 4;
            evt["episodeNumber"] = 3;
            evt["description"] = "S9 E99 - Falsch\nBeschreibung.";
        });

        Assert.Equal(4, program.SeasonNumber);
        Assert.Equal(3, program.EpisodeNumber);
        Assert.StartsWith("S9 E99", program.Overview);
    }

    /// <summary>A guide that fills only the summary must not come through empty.</summary>
    [Fact]
    public void ASummaryWithoutADescription_BecomesTheOverview()
    {
        ProgramInfo program = Parse(evt => evt["summary"] = "Danny Ocean und seine Bande.");

        Assert.Equal("Danny Ocean und seine Bande.", program.Overview);
    }

    /// <summary>A plot without a label stays a one-off broadcast.</summary>
    [Fact]
    public void AFilm_IsNotASeries()
    {
        ProgramInfo program = Parse(evt =>
        {
            evt["title"] = "Ocean's Twelve";
            evt["description"] = "Danny Ocean und seine Bande planen den naechsten Coup.";
        });

        Assert.False(program.IsSeries);
    }

    /// <summary>An empty field is not evidence.</summary>
    [Fact]
    public void EmptyFields_AreNotEvidence()
    {
        ProgramInfo program = Parse(evt =>
        {
            evt["subtitle"] = string.Empty;
            evt["serieslinkUri"] = string.Empty;
            evt["episodeOnscreen"] = string.Empty;
        });

        Assert.False(program.IsSeries);
    }
}
