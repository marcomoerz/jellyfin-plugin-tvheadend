using System;
using System.Text.RegularExpressions;

namespace TVHeadEnd.HTSP_Responses
{
    /// <summary>
    /// What a guide wrote into its text instead of into its fields.
    /// </summary>
    /// <param name="Season">The season number.</param>
    /// <param name="Episode">The episode number.</param>
    /// <param name="Title">The episode title, or <c>null</c> when only the numbering was given.</param>
    /// <param name="Rest">What is left of the text once the label is taken off the front.</param>
    public sealed record EpisodeLabel(int Season, int Episode, string? Title, string Rest);

    /// <summary>
    /// Reads an episode label off the front of a guide text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Some guides fill TVHeadend's episode fields; others write everything into one blob that
    /// begins "S4 E3 — Wir waren heute alle Gina Linetti" and continues with the plot. For the
    /// second kind TVHeadend has nothing structured to pass on, so the plugin cannot tell that
    /// the programme is an episode of anything, and Jellyfin never offers to record the series.
    /// </para>
    /// <para>
    /// Reading the label back out is a fallback, and it is deliberately a narrow one: the
    /// pattern only matches at the very start of the text. A description that opens with
    /// exactly "S4 E3" is a label, not a coincidence — and anchoring is what keeps this from
    /// finding numbers in the middle of a sentence.
    /// </para>
    /// </remarks>
    public static class EpisodeLabelReader
    {
        /// <summary>
        /// Matches "S4 E3", "S04E03" and the separators guides put after them: an em dash, an
        /// en dash, a hyphen, a colon, or nothing but space.
        /// </summary>
        private static readonly Regex Pattern = new Regex(
            @"^[ \t]*S(?<season>[0-9]{1,3})[ \t]*E(?<episode>[0-9]{1,4})(?![0-9])"
            + @"(?:[ \t]*[—–:|-][ \t]*|[ \t]+)?"
            + @"(?<title>[^\r\n]*)",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

        /// <summary>Tries to read a label off the front of <paramref name="text"/>.</summary>
        /// <returns><c>true</c> when the text begins with one.</returns>
        public static bool TryRead(string? text, out EpisodeLabel label)
        {
            label = null!;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            Match match = Pattern.Match(text);
            if (!match.Success)
            {
                return false;
            }

            // The numbers are bounded by the pattern, so they always fit.
            int season = int.Parse(match.Groups["season"].Value, System.Globalization.CultureInfo.InvariantCulture);
            int episode = int.Parse(match.Groups["episode"].Value, System.Globalization.CultureInfo.InvariantCulture);

            string title = match.Groups["title"].Value.Trim();
            string rest = text.Substring(match.Length).TrimStart('\r', '\n', ' ', '\t');

            label = new EpisodeLabel(
                season, episode, 0 == title.Length ? null : title, rest);

            return true;
        }
    }
}
