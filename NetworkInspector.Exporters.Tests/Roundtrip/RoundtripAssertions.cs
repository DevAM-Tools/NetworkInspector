// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Tests.Roundtrip;

/// <summary>
/// Shared assertions for source/exporter roundtrip tests. Each helper combines either
/// a tshark-driven external check or an in-process reimport check against the original
/// frame stream that was fed into the exporter.
/// <para>
/// The class is deliberately allocation-conscious but optimized for clarity over speed
/// since it runs only inside the test suite. All helpers throw on the first mismatch
/// to keep failure output as close to the offending frame as possible.
/// </para>
/// </summary>
internal static class RoundtripAssertions
{
    /// <summary>
    /// Tolerance in nanoseconds when comparing original vs. tshark-reported timestamps.
    /// 0 means "must match exactly". Tests pass per-format tolerance because PCAPNG
    /// preserves nanoseconds while BLF quantizes to 10 µs.
    /// </summary>
    internal const long ExactNs = 0;

    /// <summary>10 µs tolerance for BLF timestamps (the BLF native tick resolution).</summary>
    internal const long BlfTickNs = 10_000;

    /// <summary>
    /// Maps NetworkInspector <see cref="LinkType"/> values to the corresponding
    /// Wireshark <c>WTAP_ENCAP_*</c> integer that tshark reports via
    /// <c>frame.encap_type</c>. Only the link types currently used by roundtrip
    /// tests are listed; extend as needed when new exporter targets land.
    /// Reference: <c>wiretap/wtap.h</c> in the Wireshark source tree.
    /// </summary>
    private static int ExpectedTsharkEncap(LinkType linkType) => linkType switch
    {
        LinkType.Ethernet => 1,    // WTAP_ENCAP_ETHERNET
        LinkType.Flexray => 106,  // WTAP_ENCAP_FLEXRAY
        LinkType.Lin => 107,  // WTAP_ENCAP_LIN
        LinkType.CanSocketcan => 125,  // WTAP_ENCAP_SOCKETCAN
        _ => throw new InvalidOperationException(
            $"No tshark WTAP_ENCAP_* mapping defined for {linkType}. " +
            "Extend RoundtripAssertions.ExpectedTsharkEncap when adding new exporter link types.")
    };

    /// <summary>
    /// Compares the per-frame tshark output of the just-written capture against the
    /// originals that were passed to the exporter. Verifies count, encapsulation type,
    /// length (where meaningful), timestamp (within <paramref name="timestampToleranceNs"/>)
    /// and that the
    /// interface-id mapping is consistent (every original interface maps to exactly
    /// one tshark interface id, and vice versa).
    /// </summary>
    internal static void AssertTsharkMatchesOriginals(
        string filePath,
        IReadOnlyList<Frame> originals,
        long timestampToleranceNs)
    {
        List<TsharkRecord> records = TsharkVerifier.GetPacketRecords(filePath);
        if (records.Count != originals.Count)
        {
            throw new InvalidOperationException(
                $"tshark frame count mismatch: expected {originals.Count} but tshark reported {records.Count}.");
        }

        // Build a stable mapping: original FrameInterfaceId.Value → tshark interface id.
        // The mapping must be 1:1 across the whole capture.
        Dictionary<int, int> origToTshark = new();
        Dictionary<int, int> tsharkToOrig = new();

        for (int i = 0; i < records.Count; i++)
        {
            TsharkRecord rec = records[i];
            Frame original = originals[i];

            // 1. Encapsulation type — guards against the writer emitting the wrong
            //    DLT/WTAP encap (e.g. SocketCAN frames written with Ethernet header).
            int expectedEncap = ExpectedTsharkEncap(original.LinkType);
            if (rec.EncapType != expectedEncap)
            {
                throw new InvalidOperationException(
                    $"Frame {i}: tshark frame.encap_type={rec.EncapType} but expected " +
                    $"{expectedEncap} for LinkType {original.LinkType}.");
            }

            // 2. Length match. Skipped for wrapped link types: SocketCAN/DLT_LIN/
            //    DLT_FLEXRAY frames carry a per-protocol header on top of the bus
            //    payload, but tshark surfaces only the bus-native length (e.g. CAN
            //    data bytes, no SocketCAN header). The byte-exact reimport step
            //    still validates the full frame.
            bool isWrappedLinkType = original.LinkType
                is LinkType.CanSocketcan
                or LinkType.Lin
                or LinkType.Flexray;
            if (!isWrappedLinkType && rec.FrameLen != original.Data.Length)
            {
                throw new InvalidOperationException(
                    $"Frame {i}: tshark frame.len={rec.FrameLen} but original length is {original.Data.Length}.");
            }

            // 3. Raw byte equality is verified by AssertReimportMatchesOriginals
            //    (in-process reimport), not via tshark: tshark does not expose a
            //    generic per-frame raw-bytes field through `-T fields`.

            // 4. Timestamp within tolerance.
            long origNs = original.Timestamp.AsNanos;
            long delta = Math.Abs(rec.TimeEpochNanos - origNs);
            if (delta > timestampToleranceNs)
            {
                throw new InvalidOperationException(
                    $"Frame {i}: tshark timestamp {rec.TimeEpochNanos} ns differs from " +
                    $"original {origNs} ns by {delta} ns (tolerance {timestampToleranceNs} ns).");
            }

            // 5. Stable bidirectional interface mapping.
            int origIfId = original.InterfaceId.Value;
            if (origToTshark.TryGetValue(origIfId, out int existingTshark))
            {
                if (existingTshark != rec.InterfaceId)
                {
                    throw new InvalidOperationException(
                        $"Frame {i}: original interface {origIfId} previously mapped to " +
                        $"tshark interface {existingTshark}, but this frame uses tshark interface {rec.InterfaceId}.");
                }
            }
            else
            {
                if (tsharkToOrig.TryGetValue(rec.InterfaceId, out int collidingOrig))
                {
                    throw new InvalidOperationException(
                        $"Frame {i}: tshark interface {rec.InterfaceId} is already bound to " +
                        $"original interface {collidingOrig} but this frame originates from {origIfId}.");
                }

                origToTshark[origIfId] = rec.InterfaceId;
                tsharkToOrig[rec.InterfaceId] = origIfId;
            }
        }
    }

    /// <summary>
    /// Drains <paramref name="source"/> sequentially and verifies that every emitted frame
    /// matches the corresponding original (data, timestamp within tolerance, link type, and
    /// interface-name mapping derived from the source's registry).
    /// Returns the materialized list of reimported frames so callers can run additional
    /// per-format checks (random access, etc.) without a second pass.
    /// </summary>
    internal static List<Frame> AssertReimportMatchesOriginals(
        IFrameSource source,
        FrameInterfaceRegistry registry,
        IReadOnlyList<Frame> originals,
        long timestampToleranceNs)
    {
        List<Frame> reimported = new(originals.Count);
        Dictionary<int, int> origToReimported = new();
        Dictionary<int, int> reimportedToOrig = new();

        for (int i = 0; i < originals.Count; i++)
        {
            Frame? next = source.NextFrame();
            if (next is null)
            {
                throw new InvalidOperationException(
                    $"Reimport ended after {i} frames but {originals.Count} were expected.");
            }

            Frame got = next.Value;
            Frame expected = originals[i];

            if (got.LinkType != expected.LinkType)
            {
                throw new InvalidOperationException(
                    $"Frame {i}: reimport link type {got.LinkType} differs from original {expected.LinkType}.");
            }

            if (!got.Data.Span.SequenceEqual(expected.Data.Span))
            {
                throw new InvalidOperationException(
                    $"Frame {i}: reimport data differs from original.\n" +
                    $"  expected: {Convert.ToHexString(expected.Data.Span)}\n" +
                    $"  actual  : {Convert.ToHexString(got.Data.Span)}");
            }

            long delta = Math.Abs(got.Timestamp.AsNanos - expected.Timestamp.AsNanos);
            if (delta > timestampToleranceNs)
            {
                throw new InvalidOperationException(
                    $"Frame {i}: reimport timestamp {got.Timestamp.AsNanos} ns differs from " +
                    $"original {expected.Timestamp.AsNanos} ns by {delta} ns (tolerance {timestampToleranceNs} ns).");
            }

            // Verify that the per-source-interface routing survived the roundtrip.
            int origId = expected.InterfaceId.Value;
            int gotId = got.InterfaceId.Value;
            if (origToReimported.TryGetValue(origId, out int existing))
            {
                if (existing != gotId)
                {
                    throw new InvalidOperationException(
                        $"Frame {i}: original interface {origId} previously mapped to " +
                        $"reimport interface {existing}, but this frame uses {gotId}.");
                }
            }
            else
            {
                if (reimportedToOrig.TryGetValue(gotId, out int collidingOrig))
                {
                    throw new InvalidOperationException(
                        $"Frame {i}: reimport interface {gotId} already bound to original " +
                        $"interface {collidingOrig} but this frame originates from {origId}.");
                }

                origToReimported[origId] = gotId;
                reimportedToOrig[gotId] = origId;
            }

            // Also sanity-check: reimported interface info must be queryable in the new registry.
            if (got.InterfaceId.IsValid)
            {
                FrameInterfaceInfo? info = registry.Get(got.InterfaceId);
                if (info is null)
                {
                    throw new InvalidOperationException(
                        $"Frame {i}: reimport interface id {gotId} is not registered in the source registry.");
                }

                // The reimport-side interface must carry the original frame's link type.
                if (info.LinkType != got.LinkType)
                {
                    throw new InvalidOperationException(
                        $"Frame {i}: reimport interface link type {info.LinkType} differs from " +
                        $"frame link type {got.LinkType}.");
                }
            }

            reimported.Add(got);
        }

        // No trailing frames allowed.
        Frame? trailing = source.NextFrame();
        if (trailing is not null)
        {
            throw new InvalidOperationException(
                $"Reimport returned more frames than expected ({originals.Count}); " +
                $"first surplus frame id = {trailing.Value.Id.Value}.");
        }

        return reimported;
    }

    /// <summary>
    /// Helper: registers a single test interface that points at <paramref name="source"/>
    /// and starts the source. Mirrors the pattern used across the existing source tests.
    /// </summary>
    internal static FrameInterfaceRegistry StartSource(IFrameSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        return registry;
    }
}
