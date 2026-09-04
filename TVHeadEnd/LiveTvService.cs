using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;
using static TVHeadEnd.AccessTicketHandler.TicketType;

namespace TVHeadEnd
{
    public class LiveTvService : ILiveTvService
    {
        private readonly IMediaEncoder _mediaEncoder;

        private readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);

        private readonly HTSConnectionHandler _htsConnectionHandler;
        private readonly AccessTicketHandler _channelTicketHandler;

        private readonly ILogger<LiveTvService> _logger;
        public DateTime _lastRecordingChange = DateTime.MinValue;

        public LiveTvService(ILoggerFactory loggerFactory, IMediaEncoder mediaEncoder, HTSConnectionHandler connectionHandler)
        {
            //System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
            _logger = loggerFactory.CreateLogger<LiveTvService>();
            _logger.LogDebug("LiveTvService()");

            _htsConnectionHandler = connectionHandler;
            _htsConnectionHandler.setLiveTvService(this);

            {
                var lifeSpan = TimeSpan.FromSeconds(15);       // Revalidate tickets every 15 seconds
                var requestTimeout = TimeSpan.FromSeconds(10); // First request retry after 10 seconds
                var retries = 2;                               // Number of times to retry getting tickets
                _channelTicketHandler = new AccessTicketHandler(loggerFactory, _htsConnectionHandler, requestTimeout, retries, lifeSpan, Channel);
            }

            //Added for stream probing
            _mediaEncoder = mediaEncoder;
        }

        public string HomePageUrl { get { return "http://tvheadend.org/"; } }

        public string Name { get { return "TVHclient LiveTvService"; } }

        public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            string operation = $"CancelSeriesTimerAsync('{timerId}')";
            await EnsureConnectionReady(operation, cancellationToken);

            HTSMessage deleteAutorecMessage = new HTSMessage();
            deleteAutorecMessage.Method = "deleteAutorecEntry";
            deleteAutorecMessage.putField("id", timerId);

            HtspResult result = await SendAsync(deleteAutorecMessage, cancellationToken);
            ThrowOnFailure(result, operation, missingIsSuccess: true);

            _lastRecordingChange = DateTime.UtcNow;
        }

        public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        {
            string operation = $"CancelTimerAsync('{timerId}')";
            await EnsureConnectionReady(operation, cancellationToken);

            HTSMessage cancelTimerMessage = new HTSMessage();
            cancelTimerMessage.Method = "cancelDvrEntry";
            cancelTimerMessage.putField("id", timerId);

            HtspResult result = await SendAsync(cancelTimerMessage, cancellationToken);
            ThrowOnFailure(result, operation, missingIsSuccess: true);

            _lastRecordingChange = DateTime.UtcNow;
        }

        public async Task CloseLiveStream(string subscriptionId, CancellationToken cancellationToken)
        {
            await Task.Factory.StartNew(() =>
            {
                _logger.LogDebug("LiveTvService.CloseLiveStream: closed stream for subscriptionId: {id}", subscriptionId);
                return subscriptionId;
            }, cancellationToken);
        }

        public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            // Dummy method to avoid warnings
            await Task.Factory.StartNew(() => 0, cancellationToken);

            throw new NotImplementedException();
        }

        public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            string operation = $"CreateTimerAsync('{info.Name}')";
            await EnsureConnectionReady(operation, cancellationToken);

            HTSMessage createTimerMessage = new HTSMessage();
            createTimerMessage.Method = "addDvrEntry";
            createTimerMessage.putField("channelId", info.ChannelId);
            createTimerMessage.putField("start", DateTimeHelper.getUnixUTCTimeFromUtcDateTime(info.StartDate));
            createTimerMessage.putField("stop", DateTimeHelper.getUnixUTCTimeFromUtcDateTime(info.EndDate));
            createTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60));
            createTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60));
            createTimerMessage.putField("priority", _htsConnectionHandler.GetPriority()); // info.Priority delivers always 0 - no GUI
            createTimerMessage.putField("configName", _htsConnectionHandler.GetProfile());
            createTimerMessage.putField("description", info.Overview);
            createTimerMessage.putField("title", info.Name);
            createTimerMessage.putField("creator", Plugin.Instance.Configuration.Username);

            if (!string.IsNullOrEmpty(info.ProgramId)
                && int.TryParse(info.ProgramId, out int eventId)
                && eventId > 0)
            {
                createTimerMessage.putField("eventId", eventId);
            }
            else
            {
                _logger.LogWarning(
                    "LiveTvService.CreateTimerAsync: no usable EPG event id in ProgramId '{ProgramId}', "
                    + "falling back to a time based recording", info.ProgramId);
            }

            HtspResult result = await SendAsync(createTimerMessage, cancellationToken);

            // Returning normally would tell Jellyfin the timer exists, and the user would be left
            // waiting for a recording that was never scheduled.
            ThrowOnFailure(result, operation, missingIsSuccess: false);

            _lastRecordingChange = DateTime.UtcNow;
        }

        public async Task DeleteRecordingAsync(string recordingId, CancellationToken cancellationToken)
        {
            string operation = $"DeleteRecordingAsync('{recordingId}')";
            await EnsureConnectionReady(operation, cancellationToken);

            HTSMessage deleteRecordingMessage = new HTSMessage();
            deleteRecordingMessage.Method = "deleteDvrEntry";
            deleteRecordingMessage.putField("id", recordingId);

            HtspResult result = await SendAsync(deleteRecordingMessage, cancellationToken);

            // An entry already removed in TVHeadend is the desired end state. Reporting that as a
            // failure would orphan the item in Jellyfin's database with no way to remove it.
            ThrowOnFailure(result, operation, missingIsSuccess: true);

            _lastRecordingChange = DateTime.UtcNow;
        }

        public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        {
            bool loaded = await WaitForInitialLoadTask(cancellationToken);
            if (!loaded || cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("LiveTvService.GetChannelsAsync: call cancelled or timed out - returning empty list");
                return new List<ChannelInfo>();
            }

            List<ChannelInfo> list;
            try
            {
                list = (await _htsConnectionHandler.BuildChannelInfos(cancellationToken)
                    .WaitAsync(_timeout, cancellationToken)
                    .ConfigureAwait(false)).ToList();
            }
            catch (TimeoutException)
            {
                _logger.LogError("LiveTvService.GetChannelsAsync: timed out - returning empty list");
                return new List<ChannelInfo>();
            }

            foreach (var channel in list)
            {
                if (string.IsNullOrEmpty(channel.ImageUrl))
                {
                    channel.ImageUrl = _htsConnectionHandler.GetChannelImageUrl(channel.Id);
                }
            }

            return list;
        }

        public async Task<MediaSourceInfo> GetChannelStream(string channelId, string mediaSourceId, CancellationToken cancellationToken)
        {
            var ticket = await _channelTicketHandler.GetTicket(channelId, cancellationToken);

            if (_htsConnectionHandler.GetEnableSubsMaudios())
            {
                _logger.LogInformation("LiveTvService.GetChannelStream: support for live TV subtitles and multiple audio tracks is enabled");

                MediaSourceInfo livetvasset = new MediaSourceInfo();

                livetvasset.Id = channelId;

                livetvasset.Path = _htsConnectionHandler.GetHttpBaseUrlWithCredentials() + ticket.Path;
                livetvasset.Protocol = MediaProtocol.Http;
                livetvasset.AnalyzeDurationMs = 2000;
                livetvasset.SupportsDirectStream = false;
                livetvasset.RequiresClosing = true;
                livetvasset.SupportsProbing = false;
                livetvasset.Container = "mpegts";
                livetvasset.RequiresOpening = true;
                livetvasset.IsInfiniteStream  = true;

                // Probe the asset stream to determine available sub-streams
                string livetvasset_probeUrl = "" + livetvasset.Path;
                string livetvasset_source = "LiveTV";
                await ProbeStream(livetvasset, livetvasset_probeUrl, livetvasset_source, cancellationToken);

                // If enabled, force video deinterlacing for channels
                if (_htsConnectionHandler.GetForceDeinterlace())
                {
                    _logger.LogInformation("LiveTvService.GetChannelStream: force video deinterlacing for all channels and recordings is enabled");

                    foreach (MediaStream i in livetvasset.MediaStreams)
                    {
                        if (i.Type == MediaStreamType.Video && i.IsInterlaced == false)
                        {
                            i.IsInterlaced = true;
                        }
                        i.RealFrameRate = 50.0F;
                    }
                }

                return livetvasset;
            }
            else
            {
                return new MediaSourceInfo
                {
                    Id = channelId,
                    Path = _htsConnectionHandler.GetHttpBaseUrlWithoutCredentials() + ticket.Url,
                    Protocol = MediaProtocol.Http,
                    AnalyzeDurationMs = 2000,
                    SupportsDirectStream = false,
                    SupportsProbing = false,
                    Container = "mpegts",
                    MediaStreams = new List<MediaStream>
                    {
                        new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            // Set the index to -1 because we don't know the exact index of the video stream within the container
                            Index = -1,
                            // Set to true if unknown to enable deinterlacing
                            IsInterlaced = true,
                            RealFrameRate = 50.0F
                        },
                        new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            // Set the index to -1 because we don't know the exact index of the audio stream within the container
                            Index = -1
                        }
                    }
                };
            }
        }

        private async Task ProbeStream(MediaSourceInfo mediaSourceInfo, string probeUrl, string source, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Probe stream for {source}", source);
            _logger.LogInformation("Probe URL: {probeUrl}", probeUrl);

            MediaInfoRequest req = new MediaInfoRequest
            {
                MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Video,
                MediaSource = mediaSourceInfo,
                ExtractChapters = false,
            };

            var originalRuntime = mediaSourceInfo.RunTimeTicks;
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();
            MediaInfo info = await _mediaEncoder.GetMediaInfo(req, cancellationToken).ConfigureAwait(false);
            stopWatch.Stop();
            TimeSpan ts = stopWatch.Elapsed;
            string elapsedTime = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
            _logger.LogDebug("Probe RunTime {ElapsedTime}", elapsedTime);

            if (info != null)
            {
                _logger.LogDebug("Probe returned:");

                mediaSourceInfo.Bitrate = info.Bitrate;
                _logger.LogDebug("        BitRate:                    {BitRate}", info.Bitrate);

                mediaSourceInfo.Container = info.Container;
                _logger.LogDebug("        Container:                  {Container}", info.Container);

                mediaSourceInfo.MediaStreams = info.MediaStreams;
                _logger.LogDebug("        MediaStreams:               ");
                LogMediaStreamList(info.MediaStreams, "                       ");

                mediaSourceInfo.RunTimeTicks = info.RunTimeTicks;
                _logger.LogDebug("        RunTimeTicks:               {RunTimeTicks}", info.RunTimeTicks);

                mediaSourceInfo.Size = info.Size;
                _logger.LogDebug("        Size:                       {Size}", info.Size);

                mediaSourceInfo.Timestamp = info.Timestamp;
                _logger.LogDebug("        Timestamp:                  {Timestamp}", info.Timestamp);

                mediaSourceInfo.Video3DFormat = info.Video3DFormat;
                _logger.LogDebug("        Video3DFormat:              {Video3DFormat}", info.Video3DFormat);

                mediaSourceInfo.VideoType = info.VideoType;
                _logger.LogDebug("        VideoType:                  {VideoType}", info.VideoType);

                mediaSourceInfo.RequiresClosing = true;
                _logger.LogDebug("        RequiresClosing:            {RequiresClosing}", info.RequiresClosing);

                mediaSourceInfo.RequiresOpening = true;
                _logger.LogDebug("        RequiresOpening:            {RequiresOpening}", info.RequiresOpening);

                mediaSourceInfo.SupportsDirectPlay = true;
                _logger.LogDebug("        SupportsDirectPlay:         {SupportsDirectPlay}", info.SupportsDirectPlay);

                mediaSourceInfo.SupportsDirectStream = true;
                _logger.LogDebug("        SupportsDirectStream:       {SupportsDirectStream}", info.SupportsDirectStream);

                mediaSourceInfo.SupportsTranscoding = true;
                _logger.LogDebug("        SupportsTranscoding:        {SupportsTranscoding}", info.SupportsTranscoding);

                mediaSourceInfo.DefaultSubtitleStreamIndex = null;
                _logger.LogDebug("        DefaultSubtitleStreamIndex: n/a");

                if (!originalRuntime.HasValue)
                {
                    mediaSourceInfo.RunTimeTicks = null;
                    _logger.LogDebug("        Original runtime:           n/a");
                }

                var audioStream = mediaSourceInfo.MediaStreams.FirstOrDefault(i => i.Type == MediaStreamType.Audio);
                if (audioStream == null || audioStream.Index == -1)
                {
                    mediaSourceInfo.DefaultAudioStreamIndex = null;
                    _logger.LogDebug("        DefaultAudioStreamIndex:    n/a");
                }
                else
                {
                    mediaSourceInfo.DefaultAudioStreamIndex = audioStream.Index;
                    _logger.LogDebug("        DefaultAudioStreamIndex:    '{DefaultAudioStreamIndex}'", info.DefaultAudioStreamIndex);
                }
            }
            else
            {
                _logger.LogError("Cannot probe {source} stream", source);
            }
        }

        private void LogMediaStreamList(IReadOnlyList<MediaStream> theList, string prefix)
        {
            foreach (MediaStream i in theList)
                LogMediaStream(i, prefix);
        }

        private void LogMediaStream(MediaStream ms, string prefix)
        {
            _logger.LogDebug("{Prefix}AspectRatio             {AspectRatio}", prefix, ms.AspectRatio);
            _logger.LogDebug("{Prefix}AverageFrameRate        {AverageFrameRate}", prefix, ms.AverageFrameRate);
            _logger.LogDebug("{Prefix}BitDepth                {BitDepth}", prefix, ms.BitDepth);
            _logger.LogDebug("{Prefix}BitRate                 {BitRate}", prefix, ms.BitRate);
            _logger.LogDebug("{Prefix}ChannelLayout           {ChannelLayout}", prefix, ms.ChannelLayout); // Object
            _logger.LogDebug("{Prefix}Channels                {Channels}", prefix, ms.Channels);
            _logger.LogDebug("{Prefix}Codec                   {Codec}", prefix, ms.Codec); // Object
            _logger.LogDebug("{Prefix}CodecTag                {CodecTag}", prefix, ms.CodecTag); // Object
            _logger.LogDebug("{Prefix}Comment                 {Comment}", prefix, ms.Comment);
            _logger.LogDebug("{Prefix}DeliveryMethod          {DeliveryMethod}", prefix, ms.DeliveryMethod); // Object
            _logger.LogDebug("{Prefix}DeliveryUrl             {DeliveryUrl}", prefix, ms.DeliveryUrl);
            //_logger.LogDebug("{Prefix}ExternalId              {ExternalId}", prefix, ms.ExternalId);
            _logger.LogDebug("{Prefix}Height                  {Height}", prefix, ms.Height);
            _logger.LogDebug("{Prefix}Index                   {Index}", prefix, ms.Index);
            _logger.LogDebug("{Prefix}IsAnamorphic            {IsAnamorphic}", prefix, ms.IsAnamorphic);
            _logger.LogDebug("{Prefix}IsDefault               {IsDefault}", prefix, ms.IsDefault);
            _logger.LogDebug("{Prefix}IsExternal              {IsExternal}", prefix, ms.IsExternal);
            _logger.LogDebug("{Prefix}IsExternalUrl           {IsExternalUrl}", prefix, ms.IsExternalUrl);
            _logger.LogDebug("{Prefix}IsForced                {IsForced}", prefix, ms.IsForced);
            _logger.LogDebug("{Prefix}IsInterlaced            {IsInterlaced}", prefix, ms.IsInterlaced);
            _logger.LogDebug("{Prefix}IsTextSubtitleStream    {IsTextSubtitleStream}", prefix, ms.IsTextSubtitleStream);
            _logger.LogDebug("{Prefix}Language                {Language}", prefix, ms.Language);
            _logger.LogDebug("{Prefix}Level                   {Level}", prefix, ms.Level);
            _logger.LogDebug("{Prefix}PacketLength            {PacketLength}", prefix, ms.PacketLength);
            _logger.LogDebug("{Prefix}Path                    {Path}", prefix, ms.Path);
            _logger.LogDebug("{Prefix}PixelFormat             {PixelFormat}", prefix, ms.PixelFormat);
            _logger.LogDebug("{Prefix}Profile                 {Profile}", prefix, ms.Profile);
            _logger.LogDebug("{Prefix}RealFrameRate           {RealFrameRate}", prefix, ms.RealFrameRate);
            _logger.LogDebug("{Prefix}RefFrames               {RefFrames}", prefix, ms.RefFrames);
            _logger.LogDebug("{Prefix}SampleRate              {SampleRate}", prefix, ms.SampleRate);
            _logger.LogDebug("{Prefix}Score                   {Score}", prefix, ms.Score);
            _logger.LogDebug("{Prefix}SupportsExternalStream  {SupportsExternalStream}", prefix, ms.SupportsExternalStream);
            _logger.LogDebug("{Prefix}Type                    {Type}", prefix, ms.Type); // Object
            _logger.LogDebug("{Prefix}Width                   {Width}", prefix, ms.Width);
            _logger.LogDebug("{Prefix}========================", prefix);
        }

        public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            var source = await GetChannelStream(channelId, string.Empty, cancellationToken);
            return [source];
        }

        public async Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo program = null)
        {
            return await Task.Factory.StartNew(() =>
            {
                return new SeriesTimerInfo
                {
                    PostPaddingSeconds = Plugin.Instance.Configuration.Pre_Padding,
                    PrePaddingSeconds = Plugin.Instance.Configuration.Post_Padding,
                    RecordAnyChannel = true,
                    RecordAnyTime = true,
                    RecordNewOnly = false
                };
            }, cancellationToken);
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            bool loaded = await WaitForInitialLoadTask(cancellationToken);
            if (!loaded || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetProgramsAsync: call cancelled or timed out - returning empty list");
                return new List<ProgramInfo>();
            }

            HTSMessage queryEvents = new HTSMessage();
            queryEvents.Method = "getEvents";
            queryEvents.putField("channelId", Convert.ToInt32(channelId));
            queryEvents.putField("maxTime", ((DateTimeOffset)endDateUtc).ToUnixTimeSeconds());

            _logger.LogDebug("LiveTvService.GetProgramsAsync: ask TVH for events of channel '{chanid}'", channelId);

            HTSMessage response;
            try
            {
                response = await _htsConnectionHandler
                    .SendRequestAsync(queryEvents, _timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("LiveTvService.GetProgramsAsync: timeout reached while calling for events of channel '{chanid}'", channelId);
                return new List<ProgramInfo>();
            }

            return new GetEventsResponseHandler(startDateUtc, endDateUtc, _logger, cancellationToken).Parse(response);
        }

        public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        {
            bool loaded = await WaitForInitialLoadTask(cancellationToken);
            if (!loaded || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetSeriesTimersAsync: call cancelled ot timed out - returning empty list");
                return new List<SeriesTimerInfo>();
            }

            try
            {
                return await _htsConnectionHandler.BuildAutorecInfos(cancellationToken)
                    .WaitAsync(_timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("LiveTvService.GetSeriesTimersAsync: timed out - returning empty list");
                return new List<SeriesTimerInfo>();
            }
        }

        public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        {
            //  retrieve the 'Pending' recordings");

            bool loaded = await WaitForInitialLoadTask(cancellationToken);
            if (!loaded || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetTimersAsync: call cancelled or timed out - returning empty list");
                return new List<TimerInfo>();
            }

            try
            {
                return await _htsConnectionHandler.BuildPendingTimersInfos(cancellationToken)
                    .WaitAsync(_timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("LiveTvService.GetTimersAsync: timed out - returning empty list");
                return new List<TimerInfo>();
            }
        }
        public Task ResetTuner(string id, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            await CancelSeriesTimerAsync(info.Id, cancellationToken);
            _lastRecordingChange = DateTime.UtcNow;
            // TODO add if method is implemented
            // await CreateSeriesTimerAsync(info, cancellationToken);
        }

        public async Task UpdateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        {
            string operation = $"UpdateTimerAsync('{info.Id}')";
            await EnsureConnectionReady(operation, cancellationToken);

            HTSMessage updateTimerMessage = new HTSMessage();
            updateTimerMessage.Method = "updateDvrEntry";
            updateTimerMessage.putField("id", info.Id);
            updateTimerMessage.putField("startExtra", (long)(info.PrePaddingSeconds / 60));
            updateTimerMessage.putField("stopExtra", (long)(info.PostPaddingSeconds / 60));

            HtspResult result = await SendAsync(updateTimerMessage, cancellationToken);

            // Updating an entry that no longer exists cannot succeed, so a missing one is a
            // genuine failure here, unlike for the removals.
            ThrowOnFailure(result, operation, missingIsSuccess: false);

            _lastRecordingChange = DateTime.UtcNow;
        }

        /***********/
        /* Helpers */
        /***********/

        private Task<bool> WaitForInitialLoadTask(CancellationToken cancellationToken)
        {
            return _htsConnectionHandler.WaitForInitialLoadAsync(cancellationToken);
        }

        /// <summary>
        /// Refuses to go on unless TVHeadend has finished its initial load. Cancellation has to
        /// throw rather than return: returning normally reads as success to Jellyfin, which would
        /// then act on a change that never reached TVHeadend.
        /// </summary>
        private async Task EnsureConnectionReady(string operation, CancellationToken cancellationToken)
        {
            if (!await WaitForInitialLoadTask(cancellationToken))
            {
                throw new TimeoutException($"{operation}: TVHeadend connection not ready");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Performs one HTSP request/response round trip and classifies the outcome.
        /// </summary>
        private async Task<HtspResult> SendAsync(HTSMessage message, CancellationToken cancellationToken)
        {
            HTSMessage response;
            try
            {
                response = await _htsConnectionHandler
                    .SendRequestAsync(message, _timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new HtspResult.TimedOut(_timeout);
            }

            if (response.getInt("success", 0) == 1)
            {
                return new HtspResult.Ok(response);
            }

            // HTSP has no error codes, it reports the reason in one of two fields as free text.
            string reason =
                response.containsField("error") ? response.getString("error") :
                response.containsField("noaccess") ? response.getString("noaccess") :
                "unknown error";

            return reason.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? new HtspResult.NotFound()
                : new HtspResult.Failed(reason);
        }

        /// <summary>
        /// Translates a result into the exception contract Jellyfin expects. This is the single
        /// place where the internal result type meets the outside world.
        /// </summary>
        /// <param name="result">The outcome to translate.</param>
        /// <param name="operation">Operation description used in the exception message.</param>
        /// <param name="missingIsSuccess">
        /// Whether a missing entry counts as success. True for removals, where an entry that is
        /// already gone is the desired end state — reporting it as a failure would leave Jellyfin
        /// with an item it can never get rid of. False for anything that creates or changes an
        /// entry, where a missing target really is a failure.
        /// </param>
        private void ThrowOnFailure(HtspResult result, string operation, bool missingIsSuccess)
        {
            switch (result)
            {
                case HtspResult.Ok:
                    return;

                case HtspResult.NotFound when missingIsSuccess:
                    _logger.LogInformation("{Operation}: entry already gone, treating as success", operation);
                    return;

                case HtspResult.NotFound:
                    throw new InvalidOperationException($"{operation} failed: entry not found");

                case HtspResult.TimedOut timedOut:
                    throw new TimeoutException($"{operation}: timeout after {timedOut.After}");

                case HtspResult.Failed failed:
                    throw new InvalidOperationException($"{operation} failed: '{failed.Reason}'");

                default:
                    throw new InvalidOperationException($"{operation} failed: unhandled result {result}");
            }
        }

        private static string Dump(List<DayOfWeek> days)
        {
            StringBuilder sb = new StringBuilder();
            foreach (DayOfWeek dow in days)
            {
                sb.Append(dow + ", ");
            }
            string tmpResult = sb.ToString();
            if (tmpResult.EndsWith(','))
            {
                tmpResult = tmpResult[..^2];
            }
            return tmpResult;
        }
    }

    /// <summary>
    /// Outcome of an HTSP round trip. The private constructor closes the hierarchy, so the cases
    /// nested below are the only ones that can exist.
    /// </summary>
    public abstract record HtspResult
    {
        private HtspResult()
        {
        }

        /// <summary>The server acknowledged the request.</summary>
        public sealed record Ok(HTSMessage Response) : HtspResult;

        /// <summary>The entry the request referred to does not exist on the server.</summary>
        public sealed record NotFound : HtspResult;

        /// <summary>The server refused the request and gave a reason.</summary>
        public sealed record Failed(string Reason) : HtspResult;

        /// <summary>No response arrived in time.</summary>
        public sealed record TimedOut(TimeSpan After) : HtspResult;
    }
}
