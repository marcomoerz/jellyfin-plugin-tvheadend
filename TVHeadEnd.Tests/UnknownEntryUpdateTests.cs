using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.DataHelper;

namespace TVHeadEnd.Tests;

/// <summary>
/// Covers updates for entries the plugin does not know.
/// </summary>
/// <remarks>
/// TVHeadend pushes updates over the same connection that delivers the initial sync, so an
/// update can arrive for an entry that was dropped when the connection was rebuilt, or that was
/// never stored. Every message runs through one dispatch loop: an entry nobody knows must be
/// skipped, not throw.
/// </remarks>
public class UnknownEntryUpdateTests
{
    private static HTSP.HTSMessage Update(string method, int id, string title)
    {
        HTSP.HTSMessage message = new();
        message.Method = method;
        message.putField("id", id);
        message.putField("title", title);
        return HtspMessageFactory.Wire(message);
    }

    [Fact]
    public async Task DvrUpdateForAnUnknownEntry_IsSkipped()
    {
        DvrDataHelper helper = new(NullLogger<DvrDataHelper>.Instance);

        helper.dvrEntryUpdate(Update("dvrEntryUpdate", 42, "Never Seen"));

        Assert.Empty(await helper.buildDvrInfos(CancellationToken.None));
    }

    [Fact]
    public async Task AutorecUpdateForAnUnknownEntry_IsSkipped()
    {
        AutorecDataHelper helper = new(NullLogger<AutorecDataHelper>.Instance);

        HTSP.HTSMessage message = new();
        message.Method = "autorecEntryUpdate";
        message.putField("id", "does-not-exist");
        message.putField("title", "Never Seen");
        helper.autorecEntryUpdate(HtspMessageFactory.Wire(message));

        Assert.Empty(await helper.buildAutorecInfos(CancellationToken.None));
    }

    /// <summary>A known entry still has to take the update.</summary>
    [Fact]
    public async Task DvrUpdateForAKnownEntry_IsApplied()
    {
        DvrDataHelper helper = new(NullLogger<DvrDataHelper>.Instance);
        helper.dvrEntryAdd(HtspMessageFactory.DvrEntry(42, "completed", title: "Before"));

        helper.dvrEntryUpdate(Update("dvrEntryUpdate", 42, "After"));

        Assert.Equal("After", Assert.Single(await helper.buildDvrInfos(CancellationToken.None)).Name);
    }
}
