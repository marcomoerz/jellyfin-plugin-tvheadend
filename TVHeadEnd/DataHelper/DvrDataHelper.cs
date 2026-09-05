using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;


namespace TVHeadEnd.DataHelper
{
    public class DvrDataHelper
    {
        private readonly ILogger<DvrDataHelper> _logger;
        private readonly Dictionary<string, HTSMessage> _data;

        private readonly DateTime _initialDateTimeUTC = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly Func<DateTime> _utcNow;

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public DvrDataHelper(ILogger<DvrDataHelper> logger)
            : this(logger, () => DateTime.UtcNow)
        {
        }

        /// <param name="utcNow">
        /// The clock. A running recording is measured against the present, so a test has to be
        /// able to fix it.
        /// </param>
        public DvrDataHelper(ILogger<DvrDataHelper> logger, Func<DateTime> utcNow)
        {
            _logger = logger;
            _utcNow = utcNow;
            _data = new Dictionary<string, HTSMessage>();
        }

        public void clean()
        {
            lock (_data)
            {
                _data.Clear();
            }
        }

        public void dvrEntryAdd(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                if (_data.ContainsKey(id))
                {
                    _logger.LogDebug("[TVHclient] DvrDataHelper.dvrEntryAdd id already in database - skipping");
                    return;
                }
                _data.Add(id, message);
            }
        }

        public void dvrEntryUpdate(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                HTSMessage oldMessage = _data[id];
                if (oldMessage == null)
                {
                    _logger.LogDebug("[TVHclient] DvrDataHelper.dvrEntryUpdate id not in database - skipping");
                    return;
                }
                foreach (KeyValuePair<string, object> entry in message)
                {
                    if (oldMessage.containsField(entry.Key))
                    {
                        oldMessage.removeField(entry.Key);
                    }
                    oldMessage.putField(entry.Key, entry.Value);
                }
            }
        }

        public void dvrEntryDelete(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                _data.Remove(id);
            }
        }

        public Task<IEnumerable<MyRecordingInfo>> buildDvrInfos(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew<IEnumerable<MyRecordingInfo>>(() =>
            {
                lock (_data)
                {
                    List<MyRecordingInfo> result = new List<MyRecordingInfo>();
                    foreach (KeyValuePair<string, HTSMessage> entry in _data)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("[TVHclient] DvrDataHelper.buildDvrInfos: call cancelled - returning partial list");
                            return result;
                        }

                        HTSMessage m = entry.Value;
                        MyRecordingInfo ri = new MyRecordingInfo();

                        try
                        {
                            if (m.containsField("error"))
                            {
                                // When TVHeadend recordings are removed, their info can
                                // still be kept around with a status of "completed".
                                // The only way to identify them is from the error string
                                // which is set to "File missing". Use that to not show
                                // non-existing deleted recordings.
                                if (m.getString("error").Contains("missing"))
                                {
                                    continue;
                                }
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("id"))
                            {
                                ri.Id = "" + m.getInt("id");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("path"))
                            {
                                ri.Path = "" + m.getString("path");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("url"))
                            {
                                ri.Url = "" + m.getString("url");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("channel"))
                            {
                                ri.ChannelId = "" + m.getInt("channel");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("start"))
                            {
                                long unixUtc = m.getLong("start");
                                ri.StartDate = _initialDateTimeUTC.AddSeconds(unixUtc).ToUniversalTime();
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("stop"))
                            {
                                long unixUtc = m.getLong("stop");
                                ri.EndDate = _initialDateTimeUTC.AddSeconds(unixUtc).ToUniversalTime();
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("title"))
                            {
                                ri.Name = m.getString("title");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("description"))
                            {
                                ri.Overview = m.getString("description");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("subtitle"))
                            {
                                ri.EpisodeTitle = m.getString("subtitle");
                                ri.IsSeries = true;
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        ri.HasImage = false;
                        // public string ImagePath { get; set; }
                        // public string ImageUrl { get; set; }

                        try
                        {
                            if (m.containsField("state"))
                            {
                                string state = m.getString("state");
                                switch (state)
                                {
                                    case "completed":
                                        ri.Status = RecordingStatus.Completed;
                                        break;
                                    case "scheduled":
                                        ri.Status = RecordingStatus.New;
                                        continue;
                                    //break;
                                    case "missed":
                                        ri.Status = RecordingStatus.Error;
                                        break;
                                    case "recording":
                                        ri.Status = RecordingStatus.InProgress;
                                        break;

                                    default:
                                        _logger.LogCritical("[TVHclient] DvrDataHelper.buildDvrInfos: state '{state}' not handled", state);
                                        continue;
                                    //break;
                                }
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        // Path must not be set to force emby use of the LiveTvService methods!!!!
                        //if (m.containsField("path"))
                        //{
                        //    ri.Path = m.getString("path");
                        //}

                        try
                        {
                            if (m.containsField("autorecId"))
                            {
                                ri.SeriesTimerId = m.getString("autorecId");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("eventId"))
                            {
                                ri.ProgramId = "" + m.getInt("eventId");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        /*
                                public ProgramAudio? Audio { get; set; }
                                public ChannelType ChannelType { get; set; }
                                public float? CommunityRating { get; set; }
                                public List<string> Genres { get; set; }
                                public bool? IsHD { get; set; }
                                public bool IsKids { get; set; }
                                public bool IsLive { get; set; }
                                public bool IsMovie { get; set; }
                                public bool IsNews { get; set; }
                                public bool IsPremiere { get; set; }
                                public bool IsRepeat { get; set; }
                                public bool IsSeries { get; set; }
                                public bool IsSports { get; set; }
                                public string OfficialRating { get; set; }
                                public DateTime? OriginalAirDate { get; set; }
                                public string Url { get; set; }
                         */

                        ReadRecordedFile(m, ri);

                        result.Add(ri);
                    }
                    return result;
                }
            });
        }

        /// <summary>
        /// Reads what TVHeadend knows about the recorded file: which streams it contains, how
        /// large it is and how much of it exists.
        /// </summary>
        /// <remarks>
        /// TVHeadend writes the stream list when the recording starts (dvr_rec.c), so it is there
        /// for a running recording too. The per file stop time however is only written when the
        /// muxer closes, which is why a running recording has to be measured against the clock.
        /// </remarks>
        private void ReadRecordedFile(HTSMessage entry, MyRecordingInfo recording)
        {
            try
            {
                if (!entry.containsField("files"))
                {
                    return;
                }

                IList files = entry.getList("files");
                if (null == files || 0 == files.Count)
                {
                    return;
                }

                // A recording is split across files when it is interrupted; the streams are the
                // same in each, so the first one describes the whole recording.
                if (files[0] is HTSMessage first && first.containsField("info"))
                {
                    recording.MediaStreams = HtspStreamMapper.ToMediaStreams(first.getList("info"));
                }

                if (RecordingStatus.InProgress == recording.Status)
                {
                    DescribeRunningRecording(entry, files, recording);
                }
                else
                {
                    DescribeFinishedRecording(files, recording);
                }
            }
            catch (Exception ex)
            {
                // File details are an optimisation, never a reason to drop the recording.
                _logger.LogDebug(ex, "[TVHclient] DvrDataHelper: could not read file details");
            }
        }

        /// <summary>Measures a finished recording from the start and stop times of its files.</summary>
        private static void DescribeFinishedRecording(IList files, MyRecordingInfo recording)
        {
            long totalBytes = 0;
            long totalSeconds = 0;

            foreach (object? entry in files)
            {
                if (entry is not HTSMessage file)
                {
                    continue;
                }

                long size = file.getLong("size", 0);
                long start = file.getLong("start", 0);
                long stop = file.getLong("stop", 0);

                if (0 >= size || 0 >= start || stop <= start)
                {
                    continue;
                }

                totalBytes += size;
                totalSeconds += stop - start;
            }

            if (0 >= totalSeconds)
            {
                return;
            }

            recording.RecordedDuration = TimeSpan.FromSeconds(totalSeconds);
            recording.Bitrate = ToBitrate(totalBytes, totalSeconds);
        }

        /// <summary>
        /// Measures a running recording against the clock.
        /// </summary>
        /// <remarks>
        /// Its last file has no stop time yet, and the stop time on the entry is the scheduled
        /// end: it lies in the future and can still be moved. Only the elapsed time describes
        /// what a viewer can actually watch.
        /// </remarks>
        private void DescribeRunningRecording(HTSMessage entry, IList files, MyRecordingInfo recording)
        {
            long startedAt = 0;
            if (files[0] is HTSMessage first)
            {
                startedAt = first.getLong("start", 0);
            }

            if (0 >= startedAt)
            {
                startedAt = entry.getLong("start", 0);
            }

            if (0 >= startedAt)
            {
                return;
            }

            long nowUnix = (long)(_utcNow() - UnixEpoch).TotalSeconds;
            long elapsedSeconds = nowUnix - startedAt;

            // Never claim more than was scheduled: a clock skew must not invent content.
            long scheduledStop = entry.getLong("stop", 0);
            if (0 < scheduledStop && startedAt + elapsedSeconds > scheduledStop)
            {
                elapsedSeconds = scheduledStop - startedAt;
            }

            if (0 >= elapsedSeconds)
            {
                return;
            }

            recording.RecordedDuration = TimeSpan.FromSeconds(elapsedSeconds);

            // dataSize is the live size of the file, updated as it grows.
            recording.Bitrate = ToBitrate(entry.getLong("dataSize", 0), elapsedSeconds);
        }

        private static int? ToBitrate(long totalBytes, long totalSeconds)
        {
            if (0 >= totalBytes || 0 >= totalSeconds)
            {
                return null;
            }

            long bitsPerSecond = totalBytes * 8 / totalSeconds;
            return bitsPerSecond > int.MaxValue ? int.MaxValue : (int)bitsPerSecond;
        }

        public Task<IEnumerable<TimerInfo>> buildPendingTimersInfos(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew<IEnumerable<TimerInfo>>(() =>
            {
                lock (_data)
                {
                    List<TimerInfo> result = new List<TimerInfo>();
                    foreach (KeyValuePair<string, HTSMessage> entry in _data)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("[TVHclient] DvrDataHelper.buildDvrInfos: call cancelled - returning partial list");
                            return result;
                        }

                        HTSMessage m = entry.Value;
                        TimerInfo ti = new TimerInfo();

                        try
                        {
                            if (m.containsField("id"))
                            {
                                ti.Id = "" + m.getInt("id");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("channel"))
                            {
                                ti.ChannelId = "" + m.getInt("channel");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("start"))
                            {
                                long unixUtc = m.getLong("start");
                                ti.StartDate = _initialDateTimeUTC.AddSeconds(unixUtc).ToUniversalTime();
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("stop"))
                            {
                                long unixUtc = m.getLong("stop");
                                ti.EndDate = _initialDateTimeUTC.AddSeconds(unixUtc).ToUniversalTime();
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("title"))
                            {
                                ti.Name = m.getString("title");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("description"))
                            {
                                ti.Overview = m.getString("description");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("state"))
                            {
                                string state = m.getString("state");
                                switch (state)
                                {
                                    case "scheduled":
                                        ti.Status = RecordingStatus.New;
                                        break;
                                    case "recording":
                                        ti.Status = RecordingStatus.InProgress;
                                        break;
                                    default:
                                        // completed and missed entries are recordings, not timers
                                        continue;
                                }
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("startExtra"))
                            {

                                ti.PrePaddingSeconds = (int)m.getLong("startExtra") * 60;
                                ti.IsPrePaddingRequired = true;
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("stopExtra"))
                            {

                                ti.PostPaddingSeconds = (int)m.getLong("stopExtra") * 60;
                                ti.IsPostPaddingRequired = true;
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("priority"))
                            {
                                ti.Priority = m.getInt("priority");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("autorecId"))
                            {
                                ti.SeriesTimerId = m.getString("autorecId");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        try
                        {
                            if (m.containsField("eventId"))
                            {
                                ti.ProgramId = "" + m.getInt("eventId");
                            }
                        }
                        catch (InvalidCastException)
                        {
                        }

                        result.Add(ti);
                    }
                    return result;
                }
            });
        }
    }
}
