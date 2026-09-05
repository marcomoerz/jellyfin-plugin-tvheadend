using System;
using System.Collections;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace TVHeadEnd.HTSP
{
    /// <summary>
    /// Turns the stream descriptions TVHeadend sends into the media streams Jellyfin reasons about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists to avoid probing. Jellyfin decides direct play by matching
    /// <see cref="MediaStream.Codec"/> against the client profile; a stream without a codec is
    /// rejected outright and forces a transcode, no matter what the client can play. Running
    /// ffprobe to learn the codec costs seconds at every open, while TVHeadend already knows it.
    /// </para>
    /// <para>
    /// Everything here is defensive: TVHeadend omits fields depending on version, source and
    /// stream type, and a missing field must degrade the description rather than fail it.
    /// </para>
    /// </remarks>
    public static class HtspStreamMapper
    {
        /// <summary>
        /// Maps a TVHeadend stream type onto the ffmpeg codec name Jellyfin expects.
        /// </summary>
        /// <returns>The codec name, or <c>null</c> for a type we cannot name confidently.</returns>
        public static string? ToJellyfinCodec(string? htspStreamType)
        {
            if (string.IsNullOrWhiteSpace(htspStreamType))
            {
                return null;
            }

            // The names on the left are TVHeadend's, from streamtypetab in src/streaming.c; the
            // ones on the right are the ffmpeg names Jellyfin matches against. Note that
            // TVHeadend labels SCT_MP4A as "AAC" and SCT_AAC as "AAC-LATM" — the reverse of what
            // the constant names suggest.
            return htspStreamType.Trim().ToUpperInvariant() switch
            {
                "MPEG2VIDEO" => "mpeg2video",
                "H264" => "h264",
                "HEVC" => "hevc",
                "VP8" => "vp8",
                "VP9" => "vp9",
                "THEORA" => "theora",

                "MPEG2AUDIO" => "mp2",
                "AC3" => "ac3",
                "EAC3" => "eac3",
                "AC-4" => "ac4",
                "AAC" => "aac",
                "AAC-LATM" => "aac",
                "VORBIS" => "vorbis",
                "OPUS" => "opus",
                "FLAC" => "flac",

                "DVBSUB" => "dvb_subtitle",
                "TELETEXT" => "dvb_teletext",
                "TEXTSUB" => "subrip",

                _ => null,
            };
        }

        /// <summary>Classifies a TVHeadend stream type as video, audio or subtitle.</summary>
        public static MediaStreamType? ToStreamType(string? htspStreamType)
        {
            if (string.IsNullOrWhiteSpace(htspStreamType))
            {
                return null;
            }

            return htspStreamType.Trim().ToUpperInvariant() switch
            {
                "MPEG2VIDEO" or "H264" or "HEVC" or "VP8" or "VP9" or "THEORA" => MediaStreamType.Video,
                "MPEG2AUDIO" or "AC3" or "EAC3" or "AC-4" or "AAC" or "AAC-LATM"
                    or "VORBIS" or "OPUS" or "FLAC" => MediaStreamType.Audio,
                "DVBSUB" or "TELETEXT" or "TEXTSUB" => MediaStreamType.Subtitle,

                // Everything else in TVHeadend's table is signalling rather than media: NONE,
                // UNKNOWN, RAW, PCR, CAT, CA, HBBTV, RDS, MPEGTS.
                _ => null,
            };
        }

        /// <summary>
        /// Converts a TVHeadend stream list into media streams.
        /// </summary>
        /// <param name="htspStreams">
        /// The list as it arrives over HTSP. Entries that carry no recognisable type are skipped:
        /// a stream Jellyfin cannot name is worse than one it does not know about.
        /// </param>
        public static IReadOnlyList<MediaStream> ToMediaStreams(IList? htspStreams)
        {
            List<MediaStream> streams = new List<MediaStream>();
            if (htspStreams is null)
            {
                return streams;
            }

            foreach (object? entry in htspStreams)
            {
                if (entry is not HTSMessage description)
                {
                    continue;
                }

                MediaStream? stream = ToMediaStream(description, streams.Count);
                if (stream is not null)
                {
                    streams.Add(stream);
                }
            }

            return streams;
        }

        private static MediaStream? ToMediaStream(HTSMessage description, int position)
        {
            string? htspType = ReadString(description, "type");
            MediaStreamType? streamType = ToStreamType(htspType);
            string? codec = ToJellyfinCodec(htspType);

            if (streamType is null || codec is null)
            {
                return null;
            }

            MediaStream stream = new MediaStream
            {
                Type = streamType.Value,
                Codec = codec,
                // The per file info list carries no index, so the position stands in for it.
                // Where an index is present (the subscription messages carry one) TVHeadend
                // counts from one and ffmpeg from zero.
                Index = description.containsField("index")
                    ? ReadInt(description, "index") - 1
                    : position,
                Language = ReadString(description, "language"),
            };

            if (streamType == MediaStreamType.Video)
            {
                stream.Width = ReadNullableInt(description, "width");
                stream.Height = ReadNullableInt(description, "height");
                stream.RealFrameRate = ReadFrameRate(description);

                // Only claim interlacing when TVHeadend says so. Asserting it unconditionally
                // makes Jellyfin deinterlace, which by itself rules out direct play.
                stream.IsInterlaced = ReadNullableInt(description, "interlaced") == 1;
            }
            else if (streamType == MediaStreamType.Audio)
            {
                stream.Channels = ReadNullableInt(description, "channels");
                stream.SampleRate = ReadNullableInt(description, "rate");
            }

            return stream;
        }

        /// <summary>
        /// TVHeadend reports the frame duration in 90 kHz ticks, which is the inverse of the rate
        /// Jellyfin wants.
        /// </summary>
        private static float? ReadFrameRate(HTSMessage description)
        {
            const float ClockHz = 90000f;

            int? frameDuration = ReadNullableInt(description, "duration");
            if (frameDuration is null || 0 >= frameDuration.Value)
            {
                return null;
            }

            return ClockHz / frameDuration.Value;
        }

        private static string? ReadString(HTSMessage message, string field)
        {
            if (!message.containsField(field))
            {
                return null;
            }

            try
            {
                return message.getString(field);
            }
            catch (InvalidCastException)
            {
                return null;
            }
        }

        private static int ReadInt(HTSMessage message, string field)
        {
            return ReadNullableInt(message, field) ?? 0;
        }

        private static int? ReadNullableInt(HTSMessage message, string field)
        {
            if (!message.containsField(field))
            {
                return null;
            }

            try
            {
                return message.getInt(field);
            }
            catch (Exception exception) when (exception is InvalidCastException or OverflowException)
            {
                return null;
            }
        }
    }
}
