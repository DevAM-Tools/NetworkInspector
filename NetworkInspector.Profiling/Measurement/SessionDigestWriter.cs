// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Writes the session oracle: for every reference capture, a <see cref="Session"/> with a packet index, one listener
/// that re-reads every packet, one matching listener per <see cref="OracleCorpus.SessionExpressions"/> entry, and one
/// PullFill value cache. Only order-independent final results are written (per-packet field hashes, matching id sets,
/// cache rows), so listener timing cannot change the file.
/// <para>
/// <b>Completeness.</b> A slot dispatches new packets before the all-sources-completed callback in the same dispatch,
/// and the session publishes the last packet count before it reports completion, so every id has been delivered to a
/// listener when its <c>OnAllSourcesCompleted</c> runs. An id that a listener never saw is written as <c>missing</c>,
/// which makes a delivery bug visible as a byte difference instead of hiding it.
/// </para>
/// <para><b>Thread-safety:</b> the listeners run on session threads and publish their results before
/// <c>OnAllSourcesCompleted</c> sets their done event; the writer reads them only after every done event.</para>
/// </summary>
internal static class SessionDigestWriter
{
    #region Constants

    /// <summary>Upper bound for one session to finish; only trips on a hang.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(120);

    /// <summary>Buffer slots per matching read.</summary>
    private const int _MatchBufferSize = 64;

    /// <summary>Fields of the PullFill value cache.</summary>
    private static readonly string[] _CacheFields = ["udp.srcport", "tcp.srcport"];

    private const ulong _FnvOffset = 14695981039346656037UL;
    private const ulong _FnvPrime = 1099511628211UL;

    #endregion

    #region Public API

    /// <summary>Writes the oracle file. Returns 0 on success, 1 on missing inputs or a session failure.</summary>
    internal static int Run(string inputDirectory, string outPath)
    {
        string[] inputs = Directory.Exists(inputDirectory) ? Directory.GetFiles(inputDirectory, "*.pcapng") : [];
        if (inputs.Length == 0)
        {
            Console.Error.WriteLine(Invariant($"--write-session-digest: no *.pcapng files in {inputDirectory}."));
            return 1;
        }

        Array.Sort(inputs, StringComparer.Ordinal);
        using StreamWriter writer = new(outPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n";
        foreach (string input in inputs)
        {
            if (!_TryWriteInput(writer, input, out string failure))
            {
                Console.Error.WriteLine(Invariant($"--write-session-digest: {failure} ({input})."));
                return 1;
            }
        }

        return 0;
    }

    #endregion

    #region Private helpers

    private static bool _TryWriteInput(StreamWriter writer, string input, out string failure)
    {
        using Stack stack = StackHelper.CreateFullStack();
        using Session session = new(stack);

        // The session owns the source from TryAddFrameSource on; a failed add leaves it with us.
        PcapngSource source = PcapngSource.Open(input);
        if (!session.TryAddFrameSource(source, out _, out _))
        {
            source.Dispose();
            failure = "cannot add the frame source";
            return false;
        }

        AllPacketsListener all = new();
        MatchingListener[] matchers = new MatchingListener[OracleCorpus.SessionExpressions.Length];
        CacheListener cache = new();
        if (!session.TryAddListener(all, out _, out _))
        {
            failure = "cannot add the packet listener";
            return false;
        }

        for (int i = 0; i < matchers.Length; i++)
        {
            matchers[i] = new MatchingListener(OracleCorpus.SessionExpressions[i]);
            if (!session.TryAddListener(matchers[i], matchers[i].Expression, out ListenerInfo? info, out FilterError? filterError, out _))
            {
                failure = Invariant($"cannot add the matching listener '{matchers[i].Expression}': {filterError?.Message}");
                return false;
            }

            matchers[i].Id = info.Id;
        }

        if (!session.TryAddValueCache(cache, new ValueCacheRequest { FieldNames = _CacheFields }, out _, out _))
        {
            failure = "cannot add the value cache";
            return false;
        }

        if (!session.TryStart(out _)
            || !session.WaitForCompletion(_Timeout)
            || !all.Done.Wait(_Timeout)
            || !Array.TrueForAll(matchers, static m => m.Done.Wait(_Timeout))
            || !cache.Done.Wait(_Timeout))
        {
            failure = "session did not finish";
            return false;
        }

        _Write(writer, Path.GetFileName(input), session, all, matchers, cache);
        session.Shutdown();
        failure = string.Empty;
        return true;
    }

    private static void _Write(
        StreamWriter writer,
        string file,
        Session session,
        AllPacketsListener all,
        MatchingListener[] matchers,
        CacheListener cache)
    {
        int packets = session.PacketCount;
        writer.WriteLine(Invariant($"# {file}"));
        writer.WriteLine(Invariant($"packets {packets} frames {session.FrameCount}"));
        if (all.Failure is not null)
        {
            writer.WriteLine(Invariant($"listener-failure {all.Failure}"));
        }

        for (int i = 0; i < packets; i++)
        {
            writer.WriteLine(all.TryGetHash(i, out ulong hash) ? Invariant($"p {i} {hash:x16}") : Invariant($"p {i} missing"));
        }

        foreach (MatchingListener matcher in matchers)
        {
            if (matcher.Failure is not null)
            {
                writer.WriteLine(Invariant($"match-failure {matcher.Expression} {matcher.Failure}"));
            }

            StringBuilder bits = new(packets);
            int count = 0;
            for (int i = 0; i < packets; i++)
            {
                bool matched = matcher.IsMatched(i);
                count += matched ? 1 : 0;
                bits.Append(matched ? '1' : '0');
            }

            writer.WriteLine(Invariant($"match {matcher.Expression} {count} {bits}"));
        }

        foreach (string field in _CacheFields)
        {
            writer.WriteLine(_CacheLine(cache.Last, field));
        }
    }

    private static string _CacheLine(ReadOnlyValueCache? cache, string field)
    {
        if (cache is not ReadOnlyValueCache view || !view.TryGetSeries(field, out ReadOnlyValueCacheSeries<ulong> series))
        {
            return Invariant($"cache {field} none");
        }

        ulong hash = _FnvOffset;
        for (int i = 0; i < series.Count; i++)
        {
            hash = _Mix(hash, (ulong)series.GetPacketId(i));
            hash = _Mix(hash, series.GetValue(i));
        }

        return Invariant($"cache {field} {series.Count} {hash:x16}");
    }

    /// <summary>FNV-1a over the eight bytes of <paramref name="value"/>, little-endian.</summary>
    private static ulong _Mix(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash = (hash ^ ((value >> shift) & 0xFF)) * _FnvPrime;
        }

        return hash;
    }

    /// <summary>FNV-1a over every field of <paramref name="packet"/> in depth-first order (materialized).</summary>
    private static ulong _HashPacket(Packet packet)
    {
        ulong hash = _FnvOffset;
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            FieldValueData data = field.Value.Data;
            hash = _Mix(hash, (ulong)field.FieldId.Value);
            hash = _Mix(hash, (ulong)data.Type);
            foreach (char c in data.ToString(null, CultureInfo.InvariantCulture))
            {
                hash = _Mix(hash, c);
            }
        }

        return hash;
    }

    #endregion

    #region Listeners

    /// <summary>Re-reads every announced packet and stores its field hash.</summary>
    private sealed class AllPacketsListener : ISessionListener
    {
        private ulong[] _Hashes = new ulong[1024];
        private bool[] _Seen = new bool[1024];
        private Packet? _Recycle;

        internal ManualResetEventSlim Done { get; } = new(initialState: false);

        internal string? Failure { get; private set; }

        public string UiName => "OracleAllPackets";

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            _EnsureCapacity(toIndexExclusive);
            for (int i = fromIndex; i < toIndexExclusive; i++)
            {
                if (!session.TryGetPacket(new PacketId(i), _Recycle, out Packet? packet, out SessionFailure? failure))
                {
                    Failure ??= Invariant($"{i} {failure?.Code}");
                    continue;
                }

                _Recycle = packet;
                _Hashes[i] = _HashPacket(packet);
                _Seen[i] = true;
            }
        }

        public void OnAllSourcesCompleted(ISessionReader session) => Done.Set();

        internal bool TryGetHash(int id, out ulong hash)
        {
            bool seen = id < _Seen.Length && _Seen[id];
            hash = seen ? _Hashes[id] : 0;
            return seen;
        }

        private void _EnsureCapacity(int count)
        {
            if (count <= _Hashes.Length)
            {
                return;
            }

            int size = Math.Max(count, _Hashes.Length * 2);
            Array.Resize(ref _Hashes, size);
            Array.Resize(ref _Seen, size);
        }
    }

    /// <summary>
    /// Pulls matching packets with <see cref="PacketReadMode.Matching"/>. Re-scans between notifications may return an
    /// id twice; the id set de-duplicates.
    /// </summary>
    private sealed class MatchingListener(string expression) : ISessionListener
    {
        private readonly PacketRef[] _Buffer = new PacketRef[_MatchBufferSize];
        private bool[] _Matched = new bool[1024];

        internal string Expression { get; } = expression;

        internal ListenerId Id { get; set; }

        internal ManualResetEventSlim Done { get; } = new(initialState: false);

        internal string? Failure { get; private set; }

        public string UiName => "OracleMatching";

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            // A short batch means the scan reached the session's PacketCount (≥ toIndexExclusive); a full batch
            // continues after the last returned id.
            int next = fromIndex;
            while (next < toIndexExclusive)
            {
                if (!session.TryReadPackets(Id, next, _Buffer, PacketReadMode.Matching, out int count, out _,
                        out FilterError? failure, out SessionFailure? sessionFailure))
                {
                    Failure ??= failure?.Message ?? sessionFailure?.Message;
                    return;
                }

                for (int i = 0; i < count; i++)
                {
                    _Mark(_Buffer[i].Id.Value);
                }

                if (count < _Buffer.Length)
                {
                    break;
                }

                next = _Buffer[count - 1].Id.Value + 1;
            }
        }

        public void OnAllSourcesCompleted(ISessionReader session) => Done.Set();

        internal bool IsMatched(int id) => id < _Matched.Length && _Matched[id];

        private void _Mark(int id)
        {
            if (id >= _Matched.Length)
            {
                Array.Resize(ref _Matched, Math.Max(id + 1, _Matched.Length * 2));
            }

            _Matched[id] = true;
        }
    }

    /// <summary>Keeps the last read-only cache view; PullFill rows are complete when all sources completed.</summary>
    private sealed class CacheListener : IValueCacheListener
    {
        internal ManualResetEventSlim Done { get; } = new(initialState: false);

        internal ReadOnlyValueCache? Last { get; private set; }

        public string UiName => "OracleCache";

        public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive) =>
            Last = cache;

        public void OnAllSourcesCompleted(ISessionReader session) => Done.Set();
    }

    #endregion
}
