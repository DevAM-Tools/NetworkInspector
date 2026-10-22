// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Writes the filter verdict oracle: for every reference capture and every corpus expression, per-packet verdicts of a
/// field-tree <see cref="PacketFilter"/> without and with a presence index, cached re-queries, presence-prune
/// verdicts, the materialized candidate set, cache statistics, the order guard of stateful filters, and the verdicts of
/// the tree-free <see cref="FilterObserver"/>. Two builds with equal filter semantics write byte-identical files.
/// <para>Determinism: inputs in ordinal order, a fresh full stack per input, sequential evaluation, invariant culture,
/// LF line ends, UTF-8 without BOM.</para>
/// <para><b>Thread-safety:</b> not thread-safe; runs on the calling thread.</para>
/// </summary>
internal static class FilterVerdictWriter
{
    #region Constants

    /// <summary>Every seventh packet is queried twice, so the cached path is part of the oracle.</summary>
    private const int _RequeryStride = 7;

    /// <summary>Packet ids of the out-of-order probe of stateful filters: a later id first, then an earlier one.</summary>
    private const int _OrderProbeLate = 10;

    private const int _OrderProbeEarly = 5;

    #endregion

    #region Public API

    /// <summary>Writes the oracle file. Returns 0 on success, 1 on missing inputs or a parse failure.</summary>
    internal static int Run(string inputDirectory, string outPath)
    {
        string[] inputs = Directory.Exists(inputDirectory) ? Directory.GetFiles(inputDirectory, "*.pcapng") : [];
        if (inputs.Length == 0)
        {
            Console.Error.WriteLine(Invariant($"--write-filter-verdicts: no *.pcapng files in {inputDirectory}."));
            return 1;
        }

        Array.Sort(inputs, StringComparer.Ordinal);
        using StreamWriter writer = new(outPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n";
        foreach (string input in inputs)
        {
            // Fresh stack per input: stateful dissectors start from the same state in every build. The first pass
            // parses every id once, in order, and records the index; every later parse of an id is a re-parse.
            using Stack stack = StackHelper.CreateFullStack();
            using PcapngSource source = PcapngSource.Open(input);
            Frame[] frames = OracleInputs.ReadFrames(source, stack);
            if (!_TryBuildIndex(stack, frames, out PacketIndex? index))
            {
                Console.Error.WriteLine(Invariant($"--write-filter-verdicts: cannot parse {input}."));
                return 1;
            }

            string name = Path.GetFileName(input);
            foreach (string expression in OracleCorpus.FilterExpressions)
            {
                _WriteFilter(writer, name, expression, stack, frames, index);
            }

            foreach (string expression in OracleCorpus.ObserverExpressions)
            {
                _WriteObserver(writer, name, expression, stack, frames);
            }
        }

        return 0;
    }

    #endregion

    #region Private helpers

    /// <summary>First parse of every id in order, recording presence; later parses of the same ids are replays.</summary>
    private static bool _TryBuildIndex(Stack stack, Frame[] frames, [NotNullWhen(true)] out PacketIndex? index)
    {
        PacketIndex built = new(stack);
        ParseOptions options = new(FieldTreeMode.Build, index: built);
        Packet? recycle = null;
        for (int i = 0; i < frames.Length; i++)
        {
            if (!OracleInputs.TryParse(stack, new PacketId(i), frames[i], in options, ref recycle))
            {
                index = null;
                return false;
            }
        }

        index = built;
        return true;
    }

    private static void _WriteFilter(
        StreamWriter writer,
        string file,
        string expression,
        Stack stack,
        Frame[] frames,
        PacketIndex index)
    {
        writer.WriteLine(Invariant($"# {file} | {expression}"));

        // Three independent instances: no index, index (with re-queries), and the order probe.
        if (!_TryCompile(writer, expression, stack, out PacketFilter? plain)
            || !_TryCompile(writer, expression, stack, out PacketFilter? indexed)
            || !_TryCompile(writer, expression, stack, out PacketFilter? probe))
        {
            return;
        }

        _WriteRun(writer, "noindex", plain, stack, frames, index: null, requery: false);
        _WriteRun(writer, "index", indexed, stack, frames, index, requery: true);
        writer.WriteLine(Invariant($"stats evaluated={indexed.EvaluatedCount} matched={indexed.MatchedPackets.Cardinality}"));
        _WritePresence(writer, plain, index, frames.Length);
        _WriteCandidates(writer, plain, index, frames.Length);
        if (probe.IsStateful)
        {
            _WriteOrderProbe(writer, probe, stack, frames);
        }
    }

    private static bool _TryCompile(StreamWriter writer, string expression, Stack stack, [NotNullWhen(true)] out PacketFilter? filter)
    {
        FilterResult<PacketFilter> compiled = PacketFilter.Compile(expression, stack);
        if (compiled.TryGetValue(out filter))
        {
            return true;
        }

        writer.WriteLine(Invariant($"compile-error {compiled.Error.Kind} {compiled.Error.Message}"));
        return false;
    }

    /// <summary>
    /// Re-parses every id in build mode into one recycled packet (so lazy containers start unpopulated) and evaluates
    /// it. Writes one character per id: <c>1</c>, <c>0</c>, or <c>E</c>; the first failure is reported on its own line.
    /// </summary>
    private static void _WriteRun(
        StreamWriter writer,
        string label,
        PacketFilter filter,
        Stack stack,
        Frame[] frames,
        PacketIndex? index,
        bool requery)
    {
        StringBuilder bits = new(frames.Length);
        StringBuilder requeries = new();
        string? firstError = null;
        ParseOptions options = new(FieldTreeMode.Build);
        Packet? recycle = null;
        for (int i = 0; i < frames.Length; i++)
        {
            if (!OracleInputs.TryParse(stack, new PacketId(i), frames[i], in options, ref recycle))
            {
                bits.Append('P');
                continue;
            }

            bits.Append(_Evaluate(filter, recycle!, index, label, ref firstError));
            if (requery && i % _RequeryStride == 0)
            {
                requeries.Append(_Evaluate(filter, recycle!, index, label, ref firstError));
            }
        }

        writer.WriteLine(Invariant($"{label} {bits}"));
        if (requery)
        {
            writer.WriteLine(Invariant($"requery {requeries}"));
        }

        if (firstError is not null)
        {
            writer.WriteLine(firstError);
        }
    }

    private static char _Evaluate(PacketFilter filter, Packet packet, PacketIndex? index, string label, ref string? firstError)
    {
        if (filter.TryIsMatch(packet, index, out bool matched, out FilterError? failure))
        {
            return matched
                ? '1'
                : '0';
        }

        firstError ??= Invariant($"error {label} {packet.Id.Value} {failure.Kind} {failure.Message}");
        return 'E';
    }

    private static void _WritePresence(StreamWriter writer, PacketFilter filter, PacketIndex index, int count)
    {
        StringBuilder chars = new(count);
        for (int i = 0; i < count; i++)
        {
            if (!filter.TryIsPresenceCandidate(index, (uint)i, out bool isCandidate))
            {
                chars.Append('n');
                continue;
            }

            chars.Append(isCandidate ? '1' : '0');
        }

        writer.WriteLine(Invariant($"presence {chars}"));
    }

    private static void _WriteCandidates(StreamWriter writer, PacketFilter filter, PacketIndex index, int count)
    {
        if (!filter.TryBuildCandidates(index, out RoaringBitmap? candidates))
        {
            writer.WriteLine("candidates none");
            return;
        }

        StringBuilder bits = new(count);
        for (int i = 0; i < count; i++)
        {
            bits.Append(candidates.Contains((uint)i) ? '1' : '0');
        }

        writer.WriteLine(Invariant($"candidates {candidates.Cardinality} {bits}"));
    }

    /// <summary>Evaluates a later id, then an earlier one: a stateful filter must refuse the second.</summary>
    private static void _WriteOrderProbe(StreamWriter writer, PacketFilter filter, Stack stack, Frame[] frames)
    {
        if (frames.Length <= _OrderProbeLate)
        {
            writer.WriteLine("outoforder skipped");
            return;
        }

        ParseOptions options = new(FieldTreeMode.Build);
        Packet? late = null;
        Packet? early = null;
        if (!OracleInputs.TryParse(stack, new PacketId(_OrderProbeLate), frames[_OrderProbeLate], in options, ref late)
            || !OracleInputs.TryParse(stack, new PacketId(_OrderProbeEarly), frames[_OrderProbeEarly], in options, ref early))
        {
            writer.WriteLine("outoforder parse-failed");
            return;
        }

        _ = filter.TryIsMatch(late!, out _, out _);
        string outcome = filter.TryIsMatch(early!, out _, out FilterError? failure) ? "accepted" : failure.Kind.ToString();
        writer.WriteLine(Invariant($"outoforder {outcome}"));
    }

    private static void _WriteObserver(StreamWriter writer, string file, string expression, Stack stack, Frame[] frames)
    {
        writer.WriteLine(Invariant($"# {file} | observer {expression}"));
        FilterObserverOptions observerOptions = FilterObserverOptions.Default;
        FilterResult<FilterObserver> compiled = PacketFilter.Compile(expression, stack, in observerOptions);
        if (!compiled.TryGetValue(out FilterObserver? observer))
        {
            writer.WriteLine(Invariant($"compile-error observer {compiled.Error.Kind} {compiled.Error.Message}"));
            return;
        }

        ParseObserver[] observers = [new() { Observer = observer, Watch = observer.Watch }];
        ParseOptions options = new(FieldTreeMode.Skip, observers: observers);
        StringBuilder bits = new(frames.Length);
        string? firstError = null;
        Packet? recycle = null;
        for (int i = 0; i < frames.Length; i++)
        {
            if (!OracleInputs.TryParse(stack, new PacketId(i), frames[i], in options, ref recycle))
            {
                bits.Append('P');
                continue;
            }

            if (observer.TryReadMatch(out bool matched, out FilterError? failure))
            {
                bits.Append(matched ? '1' : '0');
                continue;
            }

            bits.Append('E');
            firstError ??= Invariant($"error observer {i} {failure!.Kind} {failure.Message}");
        }

        writer.WriteLine(Invariant($"observer {bits}"));
        if (firstError is not null)
        {
            writer.WriteLine(firstError);
        }
    }

    #endregion
}
