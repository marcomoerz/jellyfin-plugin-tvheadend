using TVHeadEnd.HTSP_Responses;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers reading an episode label out of a guide text.
/// </summary>
/// <remarks>
/// The fallback for guides that fill no episode fields at all. It has to be eager enough to
/// recognise the shapes those guides actually use, and narrow enough never to invent an episode
/// out of a sentence that merely contains a number.
/// </remarks>
public class EpisodeLabelTests
{
    private static EpisodeLabel Read(string text)
    {
        Assert.True(EpisodeLabelReader.TryRead(text, out EpisodeLabel label), $"nothing read from '{text}'");
        return label;
    }

    private static void ReadsNothing(string? text)
    {
        Assert.False(EpisodeLabelReader.TryRead(text, out _), $"unexpectedly read something from '{text}'");
    }

    /// <summary>The case from ProSieben MAXX, em dash and all.</summary>
    [Fact]
    public void TheEpisodeAndItsTitle_AreRecovered()
    {
        EpisodeLabel label = Read(
            "S4 E3 — Wir waren heute alle Gina Linetti\n"
            + "Noch immer befinden sich Jake und Holt auf der Jagd nach Figgis.");

        Assert.Equal(4, label.Season);
        Assert.Equal(3, label.Episode);
        Assert.Equal("Wir waren heute alle Gina Linetti", label.Title);
        Assert.Equal("Noch immer befinden sich Jake und Holt auf der Jagd nach Figgis.", label.Rest);
    }

    [Theory]
    [InlineData("S4 E3 — Titel")]      // em dash
    [InlineData("S4 E3 – Titel")]      // en dash
    [InlineData("S4 E3 - Titel")]           // hyphen
    [InlineData("S4 E3: Titel")]            // colon
    [InlineData("S4 E3 Titel")]             // nothing but a space
    [InlineData("S04E03 - Titel")]          // padded and run together
    [InlineData("S4E3 Titel")]
    public void TheSeparatorsGuidesUse_AreAllAccepted(string text)
    {
        EpisodeLabel label = Read(text);

        Assert.Equal(4, label.Season);
        Assert.Equal(3, label.Episode);
        Assert.Equal("Titel", label.Title);
    }

    /// <summary>Numbering without a title is still numbering.</summary>
    [Fact]
    public void NumberingAlone_IsEnough()
    {
        EpisodeLabel label = Read("S12 E145");

        Assert.Equal(12, label.Season);
        Assert.Equal(145, label.Episode);
        Assert.Null(label.Title);
        Assert.Equal(string.Empty, label.Rest);
    }

    /// <summary>
    /// The label has to open the text. Anything else and a plot summary mentioning a number
    /// would invent an episode.
    /// </summary>
    [Theory]
    [InlineData("Die Folge S4 E3 wird wiederholt.")]
    [InlineData("Danny Ocean und seine Bande planen S4 E3.")]
    [InlineData("Staffel 4, Folge 3 - Titel")]
    [InlineData("Noch immer befinden sich Jake und Holt auf der Jagd.")]
    [InlineData("SE4 E3")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingButALabelAtTheFront_IsIgnored(string? text)
    {
        ReadsNothing(text);
    }

    /// <summary>A four digit number after E is not a season and episode run together.</summary>
    [Fact]
    public void LongEpisodeNumbers_AreTakenWhole()
    {
        Assert.Equal(1145, Read("S1 E1145 - Titel").Episode);
    }

    /// <summary>Leading whitespace happens and must not stop the match.</summary>
    [Fact]
    public void LeadingWhitespace_IsTolerated()
    {
        Assert.Equal(3, Read("  S4 E3 - Titel").Episode);
    }

    /// <summary>Everything past the first line stays with the plot.</summary>
    [Fact]
    public void OnlyTheFirstLine_IsConsumed()
    {
        EpisodeLabel label = Read("S1 E1 - Pilot\nErste Zeile.\n\nZweite Zeile.");

        Assert.Equal("Pilot", label.Title);
        Assert.Equal("Erste Zeile.\n\nZweite Zeile.", label.Rest);
    }
}
