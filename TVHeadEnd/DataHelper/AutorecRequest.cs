using System;
using System.Text;
using MediaBrowser.Controller.LiveTv;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.DataHelper
{
    /// <summary>
    /// Builds the HTSP messages behind a recurring recording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TVHeadend calls it an autorec entry: a rule that watches the guide and schedules a
    /// recording for every event matching it. Jellyfin calls the same thing a series timer.
    /// </para>
    /// <para>
    /// Translating between the two is nothing but text and arithmetic, so it lives here rather
    /// than in the service, where it could only ever be exercised against a running server.
    /// </para>
    /// </remarks>
    public static class AutorecRequest
    {
        /// <summary>Record everything that matches (TVHeadend's DVR_AUTOREC_RECORD_ALL).</summary>
        private const int RecordEverything = 0;

        /// <summary>
        /// Record only what the guide marks as a new programme (DVR_AUTOREC_RECORD_UNIQUE).
        /// </summary>
        private const int RecordNewProgrammesOnly = 14;

        /// <summary>
        /// How far an episode may drift from its usual time. TVHeadend converts its own legacy
        /// "approximate time" field into a window of exactly this width, so a rule created here
        /// behaves like one created in its web interface.
        /// </summary>
        private const int StartWindowMinutes = 30;

        private const int MinutesPerDay = 24 * 60;

        /// <summary>No restriction. TVHeadend reads -1 that way for both ends of the window.</summary>
        public const int AnyTime = -1;

        /// <summary>Builds the message that creates the rule.</summary>
        /// <param name="timer">What Jellyfin was asked to record.</param>
        /// <param name="priority">The configured recording priority; Jellyfin has no field for it.</param>
        /// <param name="profileName">The DVR configuration TVHeadend should record with.</param>
        /// <param name="serverUtcOffset">How far the TVHeadend clock is ahead of UTC.</param>
        public static HTSMessage Create(
            SeriesTimerInfo timer, int priority, string profileName, TimeSpan serverUtcOffset)
        {
            HTSMessage message = Describe(timer, priority, profileName, serverUtcOffset);
            message.Method = "addAutorecEntry";
            return message;
        }

        /// <summary>Builds the message that changes an existing rule.</summary>
        public static HTSMessage Update(
            SeriesTimerInfo timer, int priority, string profileName, TimeSpan serverUtcOffset)
        {
            HTSMessage message = Describe(timer, priority, profileName, serverUtcOffset);
            message.Method = "updateAutorecEntry";
            message.putField("id", timer.Id);
            return message;
        }

        /// <summary>
        /// Writes the fields both messages share. TVHeadend applies exactly the fields it is
        /// sent, so create and change describe the rule the same way.
        /// </summary>
        private static HTSMessage Describe(
            SeriesTimerInfo timer, int priority, string profileName, TimeSpan serverUtcOffset)
        {
            HTSMessage message = new HTSMessage();

            message.putField("title", AsLiteralPattern(timer.Name));
            message.putField("comment", timer.Overview ?? string.Empty);
            message.putField("priority", priority);
            message.putField("configName", profileName);

            // TVHeadend counts the padding in minutes, Jellyfin in seconds.
            message.putField("startExtra", (long)(timer.PrePaddingSeconds / 60));
            message.putField("stopExtra", (long)(timer.PostPaddingSeconds / 60));

            message.putField(
                "dupDetect", timer.RecordNewOnly ? RecordNewProgrammesOnly : RecordEverything);

            // An absent channel means any channel. At the protocol version we negotiate there is
            // no value that says so, so the field has to be left out entirely.
            if (!timer.RecordAnyChannel && int.TryParse(timer.ChannelId, out int channelId))
            {
                message.putField("channelId", channelId);
            }

            // An absent day mask means every day.
            if (0 < timer.Days.Count && timer.Days.Count < 7)
            {
                message.putField("daysOfWeek", AutorecDataHelper.getDaysOfWeekFromList(timer.Days));
            }

            (int start, int end) = DescribeStartWindow(timer, serverUtcOffset);
            message.putField("start", start);
            message.putField("startWindow", end);

            return message;
        }

        /// <summary>
        /// Works out the time of day an episode may start at.
        /// </summary>
        /// <remarks>
        /// Both ends are minutes from midnight, and TVHeadend compares them against the event
        /// start in its own local time. Jellyfin works in UTC throughout, hence the offset.
        /// </remarks>
        private static (int Start, int End) DescribeStartWindow(
            SeriesTimerInfo timer, TimeSpan serverUtcOffset)
        {
            if (timer.RecordAnyTime)
            {
                return (AnyTime, AnyTime);
            }

            DateTime serverLocalStart = timer.StartDate + serverUtcOffset;
            int start = MinuteOfDay((int)serverLocalStart.TimeOfDay.TotalMinutes - StartWindowMinutes / 2);

            return (start, MinuteOfDay(start + StartWindowMinutes));
        }

        /// <summary>
        /// Works out the time of day a windowed rule is really about.
        /// </summary>
        /// <remarks>
        /// TVHeadend stores where the window opens, not the time the programme is expected at,
        /// and <see cref="DescribeStartWindow"/> puts that expected time in the middle. Reading
        /// the opening back as the start time would move the rule half a window earlier every
        /// time it was saved. The width is measured rather than assumed, so a rule made in
        /// TVHeadend's own interface with a different width survives just as well.
        /// </remarks>
        /// <param name="start">Where the window opens, or -1 for any time.</param>
        /// <param name="end">Where it closes, or -1 when the server did not say.</param>
        public static int CentreOfStartWindow(int start, int end)
        {
            if (start < 0)
            {
                return AnyTime;
            }

            if (end < 0)
            {
                return start;
            }

            return MinuteOfDay(start + MinuteOfDay(end - start) / 2);
        }

        /// <summary>Keeps a minute count inside the day; the window may cross midnight.</summary>
        private static int MinuteOfDay(int minutes)
        {
            return ((minutes % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
        }

        /// <summary>
        /// Turns a title into a pattern that matches exactly that text.
        /// </summary>
        /// <remarks>
        /// TVHeadend compiles the title of an autorec entry into a case insensitive regular
        /// expression. A programme called "Wetten, dass..? (Live)" would otherwise be read as a
        /// group with a wildcard in it, and the rule would record the wrong thing or nothing at
        /// all. Only the metacharacters are escaped: escaping spaces as well, which
        /// Regex.Escape does, is not portable across the engines TVHeadend can be built against.
        /// </remarks>
        private static string AsLiteralPattern(string? title)
        {
            const string MetaCharacters = @"\^$.|?*+()[]{}";

            StringBuilder pattern = new StringBuilder();
            foreach (char character in title ?? string.Empty)
            {
                if (MetaCharacters.Contains(character))
                {
                    pattern.Append('\\');
                }

                pattern.Append(character);
            }

            return pattern.ToString();
        }
    }
}
