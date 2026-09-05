using TVHeadEnd;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers the playback URL the plugin hands to Jellyfin.
/// </summary>
/// <remarks>
/// Jellyfin does not forward an Authorization header to TVHeadend, so wherever no access ticket
/// is available the credentials travel in the URL. That only works while both parts survive
/// being written into one: a password is free text in TVHeadend and regularly contains
/// characters that a URL reads as structure.
/// </remarks>
public class PlaybackUrlTests
{
    [Fact]
    public void PlainCredentials_AreLeftAlone()
    {
        Assert.Equal(
            "http://jellyfin:secret@tvh.local:9981",
            HTSConnectionHandler.BuildHttpBaseUrl("jellyfin", "secret", "tvh.local", 9981, string.Empty));
    }

    /// <summary>
    /// An unescaped '@' ends the authority early: everything before the last one becomes the
    /// user info and "pass@tvh.local" would be looked up as the host.
    /// </summary>
    [Fact]
    public void AtSignInThePassword_DoesNotBecomeTheHost()
    {
        string url = HTSConnectionHandler.BuildHttpBaseUrl("jellyfin", "p@ss", "tvh.local", 9981, string.Empty);

        Assert.Equal("http://jellyfin:p%40ss@tvh.local:9981", url);
        Assert.Equal("tvh.local", new Uri(url).Host);
    }

    /// <summary>A slash would otherwise end the authority and start the path.</summary>
    [Fact]
    public void SlashInThePassword_DoesNotBecomeAPath()
    {
        string url = HTSConnectionHandler.BuildHttpBaseUrl("jellyfin", "a/b", "tvh.local", 9981, string.Empty);

        Assert.Equal("tvh.local", new Uri(url).Host);
        Assert.Equal("/", new Uri(url).AbsolutePath);
    }

    /// <summary>A colon would otherwise split the user info in the wrong place.</summary>
    [Fact]
    public void ColonInThePassword_KeepsTheUserName()
    {
        string url = HTSConnectionHandler.BuildHttpBaseUrl("jellyfin", "a:b", "tvh.local", 9981, string.Empty);

        Assert.Equal("jellyfin:a%3Ab", new Uri(url).UserInfo);
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("hash#tag")]
    [InlineData("question?mark")]
    [InlineData("percent%25")]
    [InlineData("umlaut-\u00e4\u00f6\u00fc")]
    public void AnyPassword_SurvivesTheRoundTrip(string password)
    {
        string url = HTSConnectionHandler.BuildHttpBaseUrl("jellyfin", password, "tvh.local", 9981, string.Empty);

        string userInfo = new Uri(url).UserInfo;
        Assert.Equal(password, Uri.UnescapeDataString(userInfo[(userInfo.IndexOf(':') + 1)..]));
    }

    [Fact]
    public void WebRoot_IsAppended()
    {
        Assert.Equal(
            "http://jellyfin:secret@tvh.local:9981/tvh",
            HTSConnectionHandler.BuildHttpBaseUrl("jellyfin", "secret", "tvh.local", 9981, "/tvh"));
    }
}
