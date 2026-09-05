using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using TVHeadEnd.DataHelper;
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

        /// <summary>
        /// What each channel contains, so it only has to be probed once.
        /// </summary>
        private readonly ChannelStreamProfileCache _channelStreamProfiles = new ChannelStreamProfileCache();

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

            Result<HTSMessage, HtspError> result = await SendAsync(deleteAutorecMessage, cancellationToken);
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

            Result<HTSMessage, HtspError> result = await SendAsync(cancelTimerMessage, cancellationToken);
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

        /// <summary>
        /// Creates a recurring recording: a TVHeadend autorec entry that records every guide
        /// event whose title matches.
        /// </summary>
        public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            string operation = $"CreateSeriesTimerAsync('{info.Name}')";
            await EnsureConnectionReady(operation, cancellationToken);

            HTSMessage request = AutorecRequest.Create(
                info,
                _htsConnectionHandler.GetPriority(),
                _htsConnectionHandler.GetProfile(),
                await ReadServerUtcOffsetAsync(cancellationToken).ConfigureAwait(false));

            Result<HTSMessage, HtspError> result = await SendAsync(request, cancellationToken);
            ThrowOnFailure(result, operation, missingIsSuccess: false);

            _lastRecordingChange = DateTime.UtcNow;
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
            // Read straight from the configuration rather than from info.Priority. Jellyfin fills
            // that field from GetNewTimerDefaultsAsync, so it carries the same value — but only
            // as long as it keeps doing so, and there is no field for it in its interface.
            createTimerMessage.putField("priority", _htsConnectionHandler.GetPriority());
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

            Result<HTSMessage, HtspError> result = await SendAsync(createTimerMessage, cancellationToken);

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

            Result<HTSMessage, HtspError> result = await SendAsync(deleteRecordingMessage, cancellationToken);

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

                await DescribeChannelAsync(channelId, livetvasset, cancellationToken).ConfigureAwait(false);

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
                MediaSourceInfo livetvasset = new MediaSourceInfo
                {
                    Id = channelId,
                    Path = _htsConnectionHandler.GetHttpBaseUrlWithoutCredentials() + ticket.Url,
                    Protocol = MediaProtocol.Http,
                    AnalyzeDurationMs = 2000,
                    SupportsDirectStream = false,
                    SupportsProbing = false,
                    Container = "mpegts",

                    // Jellyfin only hands out an open token, and so only asks for a fresh
                    // playback ticket, when the source says it has to be opened. Without this
                    // the client replays whatever ticket was in the listing.
                    RequiresOpening = true,
                    RequiresClosing = true,
                    IsInfiniteStream = true,

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

                await DescribeChannelAsync(channelId, livetvasset, cancellationToken).ConfigureAwait(false);

                return livetvasset;
            }
        }

        /// <summary>
        /// Fills in what the channel actually contains, probing only when it is not already known.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Jellyfin decides direct play by matching each stream codec against the client profile,
        /// so a channel described without codecs is always transcoded. Learning them means
        /// probing, which blocks playback for as long as ffmpeg needs to read the stream.
        /// </para>
        /// <para>
        /// A channel keeps its codecs for years, so the answer is remembered and the wait is paid
        /// once rather than on every playback. A probe that fails leaves the caller with the
        /// description it already had.
        /// </para>
        /// </remarks>
        private async Task DescribeChannelAsync(
            string channelId, MediaSourceInfo asset, CancellationToken cancellationToken)
        {
            ChannelStreamProfile? known = _channelStreamProfiles.Get(channelId);
            if (null != known)
            {
                _logger.LogDebug(
                    "LiveTvService: channel '{channel}' is already known, skipping the probe", channelId);
                ChannelStreamProfileCache.ApplyTo(known, asset);
                return;
            }

            try
            {
                await ProbeStream(asset, asset.Path, "LiveTV", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Playing with a rough description beats not playing at all.
                _logger.LogWarning(ex,
                    "LiveTvService: could not probe channel '{channel}', continuing without codecs", channelId);
                return;
            }

            if (_channelStreamProfiles.Remember(channelId, asset))
            {
                _logger.LogDebug(
                    "LiveTvService: remembered {count} streams for channel '{channel}'",
                    asset.MediaStreams?.Count ?? 0, channelId);
            }
        }

        /// <summary>
        /// Forgets what was measured about the channels.
        /// </summary>
        /// <remarks>
        /// Called when the configuration changes or the connection is rebuilt: a different
        /// server, streaming profile or transcoding setting can change what a channel delivers,
        /// and a description that no longer fits is worse than none.
        /// </remarks>
        public void ForgetChannelDescriptions()
        {
            _logger.LogDebug("LiveTvService: forgetting the remembered channel descriptions");
            _channelStreamProfiles.Clear();
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

                // How the stream is delivered is the caller's decision, not the probe's.
                // Overwriting it here made the first playback of a channel behave differently
                // from every later one, because a remembered description does not come through
                // this method at all.

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

        /// <summary>
        /// Describes what a new recording looks like before the user has changed anything.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Not all of this survives. Jellyfin overwrites <c>RecordAnyTime</c> and <c>Days</c>
        /// with "at any time, every day" the moment this returns, and it takes <c>Priority</c>
        /// out of here for every timer it creates — those two facts are why the values below are
        /// what they are. The rest reaches the dialog as the state its fields start in.
        /// </para>
        /// <para>
        /// Recording on any channel is the one that has to be right. TVHeadend matches a rule by
        /// title, so "Tatort" without a channel records it on every station that carries it: ARD,
        /// ONE, and every regional channel repeating it, all at once. The programme the user
        /// picked names a channel, and that is the channel they meant.
        /// </para>
        /// </remarks>
        public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        {
            SeriesTimerInfo defaults = new SeriesTimerInfo
            {
                PrePaddingSeconds = Plugin.Instance.Configuration.Pre_Padding,
                PostPaddingSeconds = Plugin.Instance.Configuration.Post_Padding,

                // Jellyfin reads the priority of every new timer out of these defaults, and it
                // has no field of its own for it.
                Priority = _htsConnectionHandler.GetPriority(),

                RecordAnyChannel = false,
                RecordAnyTime = true,

                // A repeat of something already recorded is rarely what was meant.
                RecordNewOnly = true,
            };

            if (null != program)
            {
                defaults.Name = program.Name;
                defaults.ChannelId = program.ChannelId;
                defaults.ProgramId = program.Id;
                defaults.SeriesId = program.SeriesId;
                defaults.StartDate = program.StartDate;

                // Asking to record a repeat means the repeat is the point.
                defaults.RecordNewOnly = !program.IsRepeat;
            }

            return Task.FromResult(defaults);
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

            Result<HTSMessage, HtspError> reply = await _htsConnectionHandler
                .SendRequestAsync(queryEvents, _timeout, cancellationToken)
                .ConfigureAwait(false);

            if (!reply.IsSuccess)
            {
                // An empty guide is better than a failed page: the rest of the channels may work.
                _logger.LogDebug(
                    "LiveTvService.GetProgramsAsync: no events for channel '{chanid}': {error}",
                    channelId, reply.Error.Describe());
                return new List<ProgramInfo>();
            }

            return new GetEventsResponseHandler(startDateUtc, endDateUtc, _logger, cancellationToken).Parse(reply.Value);
        }

        public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        {
            bool loaded = await WaitForInitialLoadTask(cancellationToken);
            if (!loaded || cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("LiveTvService.GetSeriesTimersAsync: call cancelled ot timed out - returning empty list");
                return new List<SeriesTimerInfo>();
            }

            TimeSpan serverUtcOffset = await ReadServerUtcOffsetAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await _htsConnectionHandler.BuildAutorecInfos(cancellationToken, serverUtcOffset)
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

        /// <summary>
        /// Changes a recurring recording in place.
        /// </summary>
        /// <remarks>
        /// TVHeadend applies the fields it is sent and keeps the rest, and this sends every
        /// field the rule was created from — so what Jellyfin shows is what the rule becomes.
        /// A missing entry is a genuine failure: there is nothing to change.
        /// </remarks>
        public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        {
            string operation = $"UpdateSeriesTimerAsync('{info.Id}')";
            await EnsureConnectionReady(operation, cancellationToken);

            HTSMessage request = AutorecRequest.Update(
                info,
                _htsConnectionHandler.GetPriority(),
                _htsConnectionHandler.GetProfile(),
                await ReadServerUtcOffsetAsync(cancellationToken).ConfigureAwait(false));

            Result<HTSMessage, HtspError> result = await SendAsync(request, cancellationToken);
            ThrowOnFailure(result, operation, missingIsSuccess: false);

            _lastRecordingChange = DateTime.UtcNow;
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

            Result<HTSMessage, HtspError> result = await SendAsync(updateTimerMessage, cancellationToken);

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
        /// Reads how far the TVHeadend clock is ahead of UTC.
        /// </summary>
        /// <remarks>
        /// A recurring recording says "around eight in the evening", and TVHeadend matches that
        /// against the guide in its own local time. Jellyfin works in UTC throughout, so without
        /// this the rule sits an hour or two off in most of Europe — far enough to miss the
        /// programme. A server that will not answer is taken to run on UTC: a rule at the wrong
        /// hour still beats no rule at all.
        /// </remarks>
        private async Task<TimeSpan> ReadServerUtcOffsetAsync(CancellationToken cancellationToken)
        {
            HTSMessage request = new HTSMessage();
            request.Method = "getSysTime";

            // Deliberately not SendAsync: getSysTime answers with the time itself and carries no
            // success field, so interpreting it as an outcome would read every reply as refused.
            Result<HTSMessage, HtspError> reply = await _htsConnectionHandler
                .SendRequestAsync(request, _timeout, cancellationToken)
                .ConfigureAwait(false);

            if (!reply.IsSuccess)
            {
                _logger.LogWarning(
                    "LiveTvService: could not read the TVHeadend clock ({error}), assuming UTC",
                    reply.Error.Describe());
                return TimeSpan.Zero;
            }

            TimeSpan offset = TimeSpan.FromMinutes(reply.Value.getInt("gmtoffset", 0));
            _logger.LogDebug("LiveTvService: the TVHeadend clock is {offset} ahead of UTC", offset);
            return offset;
        }

        /// <summary>
        /// Performs one HTSP round trip and classifies what came back.
        /// </summary>
        private async Task<Result<HTSMessage, HtspError>> SendAsync(HTSMessage message, CancellationToken cancellationToken)
        {
            return (await _htsConnectionHandler
                .SendRequestAsync(message, _timeout, cancellationToken)
                .ConfigureAwait(false))
                .AndThen(Interpret);
        }

        /// <summary>
        /// Reads the outcome out of a reply. HTSP answers every request with success = 1 or a
        /// reason in one of two fields; it has no error codes, so an entry that is already gone
        /// can only be recognised by the wording.
        /// </summary>
        private static Result<HTSMessage, HtspError> Interpret(HTSMessage response)
        {
            if (response.getInt("success", 0) == 1)
            {
                return response;
            }

            string reason =
                response.containsField("error") ? response.getString("error") :
                response.containsField("noaccess") ? response.getString("noaccess") :
                "unknown error";

            if (reason.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return new HtspError.NotFound();
            }

            return new HtspError.Rejected(reason);
        }

        /// <summary>
        /// Translates an outcome into the exception contract Jellyfin expects. The single place
        /// where the internal error model meets the outside world.
        /// </summary>
        /// <param name="result">The outcome to translate.</param>
        /// <param name="operation">Operation description used in the exception message.</param>
        /// <param name="missingIsSuccess">
        /// Whether a missing entry counts as success. True for removals, where an entry that is
        /// already gone is the desired end state — reporting it as a failure would leave Jellyfin
        /// with an item it can never get rid of. False for anything that creates or changes an
        /// entry, where a missing target really is a failure.
        /// </param>
        private void ThrowOnFailure(Result<HTSMessage, HtspError> result, string operation, bool missingIsSuccess)
        {
            if (result.IsSuccess)
            {
                return;
            }

            switch (result.Error)
            {
                case HtspError.NotFound when missingIsSuccess:
                    _logger.LogInformation("{Operation}: entry already gone, treating as success", operation);
                    return;

                case HtspError.Timeout timeout:
                    throw new TimeoutException($"{operation}: {timeout.Describe()}");

                case HtspError.Cancelled:
                    throw new OperationCanceledException($"{operation}: {result.Error.Describe()}");

                default:
                    throw new InvalidOperationException($"{operation} failed: {result.Error.Describe()}");
            }
        }
    }

}
