using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace TVHeadEnd
{
    /// <summary>What a probe found out about a channel.</summary>
    /// <param name="Streams">The streams the channel carries.</param>
    /// <param name="Container">The container as ffprobe named it.</param>
    /// <param name="Bitrate">Bits per second, or <c>null</c> when the probe could not tell.</param>
    /// <param name="LearnedAtUtc">When this was measured.</param>
    public sealed record ChannelStreamProfile(
        IReadOnlyList<MediaStream> Streams,
        string? Container,
        int? Bitrate,
        DateTime LearnedAtUtc);

    /// <summary>
    /// Remembers what a channel contains, so it only has to be probed once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probing a live channel means running ffprobe against the stream and waiting for it before
    /// playback can start — seconds, every single time. What it finds barely ever changes: a
    /// broadcaster keeps its codecs for years. Measuring once and reusing the answer removes that
    /// wait from every playback but the first.
    /// </para>
    /// <para>
    /// It does expire, because "barely ever" is not "never": a broadcaster switching from MPEG-2
    /// to H.264 must not leave us describing the channel wrongly forever.
    /// </para>
    /// </remarks>
    public sealed class ChannelStreamProfileCache
    {
        private readonly ConcurrentDictionary<string, ChannelStreamProfile> _profiles = new();
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _lifetime;

        public ChannelStreamProfileCache()
            : this(TimeSpan.FromHours(12), () => DateTime.UtcNow)
        {
        }

        /// <param name="lifetime">How long a measurement stays trustworthy.</param>
        /// <param name="utcNow">The clock, so expiry can be tested without waiting.</param>
        public ChannelStreamProfileCache(TimeSpan lifetime, Func<DateTime> utcNow)
        {
            _lifetime = lifetime;
            _utcNow = utcNow;
        }

        /// <summary>Returns what is known about a channel, or <c>null</c> when it has to be probed.</summary>
        public ChannelStreamProfile? Get(string channelId)
        {
            if (string.IsNullOrEmpty(channelId) || !_profiles.TryGetValue(channelId, out ChannelStreamProfile? profile))
            {
                return null;
            }

            if (_utcNow() - profile.LearnedAtUtc >= _lifetime)
            {
                _profiles.TryRemove(channelId, out _);
                return null;
            }

            return profile;
        }

        /// <summary>
        /// Records what a probe found. A probe that produced no streams is not worth keeping: it
        /// would suppress every later attempt to find out.
        /// </summary>
        /// <returns><c>true</c> when the result was worth remembering.</returns>
        public bool Remember(string channelId, MediaSourceInfo probed)
        {
            if (string.IsNullOrEmpty(channelId) || null == probed)
            {
                return false;
            }

            List<MediaStream> streams = (probed.MediaStreams ?? new List<MediaStream>())
                .Where(stream => !string.IsNullOrEmpty(stream.Codec))
                .ToList();

            if (0 == streams.Count)
            {
                return false;
            }

            _profiles[channelId] = new ChannelStreamProfile(
                streams, probed.Container, probed.Bitrate, _utcNow());
            return true;
        }

        /// <summary>Applies what is known to a media source that has not been probed.</summary>
        public static void ApplyTo(ChannelStreamProfile profile, MediaSourceInfo target)
        {
            // Copy the streams: Jellyfin mutates them, for instance when forcing deinterlacing,
            // and that must not corrupt what we remembered.
            target.MediaStreams = profile.Streams.Select(Copy).ToList();

            if (!string.IsNullOrEmpty(profile.Container))
            {
                target.Container = profile.Container;
            }

            if (profile.Bitrate.HasValue)
            {
                target.Bitrate = profile.Bitrate;
            }
        }

        /// <summary>Forgets everything, for instance after reconnecting to a different server.</summary>
        public void Clear()
        {
            _profiles.Clear();
        }

        private static MediaStream Copy(MediaStream stream)
        {
            return new MediaStream
            {
                Type = stream.Type,
                Index = stream.Index,
                Codec = stream.Codec,
                Language = stream.Language,
                Width = stream.Width,
                Height = stream.Height,
                BitRate = stream.BitRate,
                Channels = stream.Channels,
                SampleRate = stream.SampleRate,
                IsInterlaced = stream.IsInterlaced,
                RealFrameRate = stream.RealFrameRate,
                AspectRatio = stream.AspectRatio,
                Profile = stream.Profile,
                Level = stream.Level,
                IsDefault = stream.IsDefault,
                IsForced = stream.IsForced,
            };
        }
    }
}
