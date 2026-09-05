using System;

namespace TVHeadEnd.HTSP
{
    /// <summary>
    /// Why an HTSP operation did not produce a usable reply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closed hierarchy rather than a string, because callers act on the category: a removal
    /// treats <see cref="NotFound"/> as success, a timeout is worth retrying, and a rejection is
    /// not. Encoded as text those distinctions can only be recovered by searching for words.
    /// </para>
    /// <para>
    /// The private constructor closes the hierarchy: the cases nested below are the only ones
    /// that can exist.
    /// </para>
    /// </remarks>
    public abstract record HtspError
    {
        private HtspError()
        {
        }

        /// <summary>Human readable text for logs and error messages.</summary>
        public abstract string Describe();

        /// <summary>No reply arrived within the caller's deadline.</summary>
        public sealed record Timeout(TimeSpan After) : HtspError
        {
            public override string Describe() => $"no reply within {After}";
        }

        /// <summary>The caller cancelled before the reply arrived.</summary>
        public sealed record Cancelled : HtspError
        {
            public override string Describe() => "the request was cancelled";
        }

        /// <summary>The connection died, so the reply will never come.</summary>
        public sealed record ConnectionClosed : HtspError
        {
            public override string Describe() => "the connection to TVHeadend is closed";
        }

        /// <summary>
        /// The entry the request referred to does not exist on the server. For a removal this is
        /// the desired end state, not a failure.
        /// </summary>
        public sealed record NotFound : HtspError
        {
            public override string Describe() => "the entry does not exist";
        }

        /// <summary>TVHeadend refused the request and gave a reason.</summary>
        public sealed record Rejected(string Reason) : HtspError
        {
            public override string Describe() => Reason;
        }

        /// <summary>Something below the protocol went wrong.</summary>
        public sealed record Transport(string Detail) : HtspError
        {
            public override string Describe() => Detail;
        }
    }
}
