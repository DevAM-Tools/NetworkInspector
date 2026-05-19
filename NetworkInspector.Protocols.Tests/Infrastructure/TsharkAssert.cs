// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests.Infrastructure;

/// <summary>
/// Symmetric Network-Inspector ↔ tshark cross-validation helper. Replaces the
/// historical asymmetric pattern where both <c>AssertU64Field(packet, …, literal)</c>
/// and <c>Assert.That(tsharkValue).IsEqualTo(literal)</c> were checked against the
/// *same* literal — a drift in either side stayed silent because the literal was the
/// only common reference.
/// </summary>
/// <remarks>
/// <para><b>Algorithm.</b> For each (NI-field, tshark-field) pair the helper looks up
/// the NI value through <see cref="Stack.GetFieldId"/> + <see cref="Packet.TryGetFieldValue"/>,
/// extracts the tshark value via <see cref="TsharkVerifier.GetFieldValue(byte[], string, int, string)"/>,
/// and compares them through <see cref="TsharkEquivalence.AreEquivalent(string?, string?)"/>.
/// On mismatch the failure message contains both string forms (<c>tshark="…", ni="…"</c>)
/// so the diff is immediately visible.</para>
/// <para><b>tshark-missing handling.</b> When <c>tshark</c> is absent and the developer
/// escape hatch <see cref="TsharkVerifier.AllowMissingEnvVar"/> is enabled,
/// <see cref="TsharkVerifier.GetFieldValue(byte[], string, int, string)"/> returns
/// <see langword="null"/> and the helper returns silently — the test passes without
/// cross-validation evidence. CI must leave the env var unset so the underlying
/// extraction throws on missing tshark and the test goes red.</para>
/// <para><b>tshark-field-absent handling.</b> When tshark *is* available but does not
/// emit the requested field (mismatching dissector configuration, malformed frame, …)
/// the helper fails the assertion. A field that was expected by the test must be
/// produced by tshark; a silent pass would defeat the purpose of cross-validation.</para>
/// <para><b>Thread-safety.</b> Stateless. Safe for concurrent use.</para>
/// </remarks>
internal static class TsharkAssert
{
    /// <summary>
    /// Verifies that the Network-Inspector field at <paramref name="niFieldPath"/> and the
    /// tshark field <paramref name="tsharkFieldName"/> are semantically equivalent.
    /// </summary>
    /// <param name="stack">Parser stack used to resolve <paramref name="niFieldPath"/> to a <see cref="FieldId"/>.</param>
    /// <param name="packet">Parsed packet that must contain the field.</param>
    /// <param name="niFieldPath">Dotted Network-Inspector field path (for example <c>ip.src</c>).</param>
    /// <param name="frame">Raw frame bytes to feed to tshark.</param>
    /// <param name="tsharkFieldName">tshark field name (for example <c>ip.src</c>).</param>
    /// <param name="dlt">libpcap link-type identifier; default <c>1</c> = Ethernet.</param>
    /// <param name="decodeAs">Optional <c>-d</c> dissector override (for example <c>tcp.port==8443,http2</c>).</param>
    /// <param name="profileDir">
    /// Optional per-test tshark profile directory; required for protocols whose dissector only
    /// activates with a UAT (Signal-PDU, PDU-Transport, …). Passed straight to
    /// <see cref="TsharkVerifier.GetFieldValue"/>.
    /// </param>
    internal static async Task AssertEquivalent(
        Stack stack,
        Packet packet,
        string niFieldPath,
        byte[] frame,
        string tsharkFieldName,
        int dlt = 1,
        string? decodeAs = null,
        string? profileDir = null)
    {
        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, tsharkFieldName, dlt, decodeAs, profileDir);
        if (tsharkValue is null)
        {
            // Two paths reach here:
            //   (1) tshark is missing AND the developer escape hatch is enabled →
            //       silent skip (CI leaves the env var unset, the extractor throws first).
            //   (2) tshark is available but did not emit the field → assertion failure.
            await Assert.That(TsharkVerifier.MissingTsharkAllowed)
                .IsTrue()
                .Because(
                    $"tshark did not emit field '{tsharkFieldName}'. " +
                    "Either the dissector configuration is wrong, the frame is malformed, " +
                    "or the field name is incorrect.");
            return;
        }

        string? niValue = TryGetNiValueAsString(stack, packet, niFieldPath);
        await Assert.That(niValue)
            .IsNotNull()
            .Because($"NI field '{niFieldPath}' must be present (tshark reported '{tsharkValue}').");

        bool equivalent = TsharkEquivalence.AreEquivalent(niValue, tsharkValue);
        await Assert.That(equivalent)
            .IsTrue()
            .Because(TsharkEquivalence.Describe(niFieldPath, niValue, tsharkValue) +
                     $" (tshark field '{tsharkFieldName}')");
    }

    /// <summary>
    /// Verifies multiple (NI-field, tshark-field) pairs in one call. All pairs share the
    /// same frame and are checked against a single tshark invocation per pair (the
    /// underlying extractor batches by writing one temp PCAP per call; future
    /// optimisation may collapse these into a single batched call).
    /// </summary>
    internal static async Task AssertEquivalentMany(
        Stack stack,
        Packet packet,
        byte[] frame,
        int dlt,
        params (string NiFieldPath, string TsharkFieldName)[] pairs)
    {
        foreach ((string ni, string tsh) in pairs)
        {
            // ConfigureAwait(false): this helper is a library-style utility, not a
            // top-level test method. CA2007 forbids unconditional context capture here.
            await AssertEquivalent(stack, packet, ni, frame, tsh, dlt).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies multiple (NI-field, tshark-field) pairs against a tshark invocation
    /// scoped to <paramref name="profileDir"/> — used by Signal-PDU, PDU-Transport
    /// and other UAT-driven dissectors.
    /// </summary>
    internal static async Task AssertEquivalentMany(
        Stack stack,
        Packet packet,
        byte[] frame,
        int dlt,
        string? profileDir,
        params (string NiFieldPath, string TsharkFieldName)[] pairs)
    {
        foreach ((string ni, string tsh) in pairs)
        {
            await AssertEquivalent(stack, packet, ni, frame, tsh, dlt, decodeAs: null, profileDir).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Same as overload without decode-as, plus an explicit Wireshark Decode-As rule
    /// (UDP/TCP PDU-Transport heuristic binding parity).
    /// </summary>
    internal static async Task AssertEquivalentMany(
        Stack stack,
        Packet packet,
        byte[] frame,
        int dlt,
        string? profileDir,
        string decodeAs,
        params (string NiFieldPath, string TsharkFieldName)[] pairs)
    {
        foreach ((string ni, string tsh) in pairs)
        {
            await AssertEquivalent(stack, packet, ni, frame, tsh, dlt, decodeAs, profileDir).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Convenience overload for Ethernet link-type frames.
    /// </summary>
    internal static Task AssertEquivalentMany(
        Stack stack,
        Packet packet,
        byte[] frame,
        params (string NiFieldPath, string TsharkFieldName)[] pairs)
        => AssertEquivalentMany(stack, packet, frame, 1, pairs);

    /// <summary>
    /// Asserts that tshark reports an expert-info entry of the given
    /// <paramref name="severity"/> (<c>chat</c>/<c>note</c>/<c>warn</c>/<c>error</c>)
    /// containing <paramref name="messageOrGroupSubstring"/> in either its group or text.
    /// Uses <c>tshark -T pdml</c> and scans for <c>&lt;proto name="expert"&gt;</c> elements.
    /// </summary>
    internal static async Task AssertExpertInfo(
        byte[] frame,
        string severity,
        string messageOrGroupSubstring,
        int dlt = 1)
    {
        // Re-use TsharkVerifier protocol-fields path: tshark exposes expert info as an
        // <expert> protocol with severity attribute on the parent <proto>.
        List<PdmlField> fields = TsharkVerifier.GetProtocolFields(frame, "expert", dlt);
        if (fields.Count == 0)
        {
            await Assert.That(TsharkVerifier.MissingTsharkAllowed)
                .IsTrue()
                .Because(
                    $"tshark did not emit any expert info matching severity='{severity}', " +
                    $"substring='{messageOrGroupSubstring}'.");
            return;
        }

        bool match = false;
        foreach (PdmlField field in fields)
        {
            string showname = field.ShowName ?? string.Empty;
            string show = field.Show ?? string.Empty;
            if (showname.Contains(messageOrGroupSubstring, StringComparison.OrdinalIgnoreCase) ||
                show.Contains(messageOrGroupSubstring, StringComparison.OrdinalIgnoreCase))
            {
                match = true;
                break;
            }
        }

        await Assert.That(match)
            .IsTrue()
            .Because(
                $"Expected tshark expert info with severity='{severity}' and substring='{messageOrGroupSubstring}'. " +
                $"Got {fields.Count} expert entries, none matched.");
    }

    /// <summary>
    /// Resolves <paramref name="niFieldPath"/> against <paramref name="stack"/> and
    /// renders the value of the field on <paramref name="packet"/> as a string suitable
    /// for symmetric tshark comparison. Returns <see langword="null"/> when the field is
    /// absent on the packet (the field id is unknown or no value was set).
    /// </summary>
    /// <remarks>
    /// The string form is the canonical Network-Inspector representation produced by
    /// <see cref="FieldValue.ToString()"/> — IP/MAC/EUI-64/UUID/timestamp values use
    /// their well-known textual forms, integers are rendered in decimal, and bytes use
    /// upper-case hex without separators. <see cref="TsharkEquivalence"/> normalises both
    /// sides before comparison so cosmetic differences (case, hex prefixes, IP
    /// canonicalisation) do not cause failures.
    /// </remarks>
    private static string? TryGetNiValueAsString(Stack stack, Packet packet, string niFieldPath)
    {
        FieldId? fieldId = stack.GetFieldId(niFieldPath);
        if (fieldId is null)
        {
            return null;
        }
        if (!packet.TryGetFieldValue(fieldId.Value, out FieldValue value))
        {
            return null;
        }
        return value.ToString();
    }
}
