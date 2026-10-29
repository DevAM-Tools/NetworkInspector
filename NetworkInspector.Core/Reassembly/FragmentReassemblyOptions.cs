// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>What the reassembler does when a fragment covers bytes that a stored fragment already covers.</summary>
public enum FragmentOverlapPolicy : byte
{
    /// <summary>
    /// IPv4 behavior: a second fragment at an already used offset replaces the stored one when it is larger; overlaps at
    /// different offsets are stored as they are and the later fragment overwrites earlier bytes when the datagram is built.
    /// Tolerates a sender that re-emits a longer fragment after switching to a smaller MTU. RFC 791 leaves the strategy open.
    /// </summary>
    KeepLargestDuplicate,

    /// <summary>
    /// IPv6 behavior (RFC 5722): a fragment whose bytes intersect a stored fragment at a different offset discards the whole
    /// datagram. A duplicate at the same offset still follows the largest-wins rule.
    /// </summary>
    DiscardDatagram,

    /// <summary>
    /// The first received bytes win: only the parts of a new fragment that no stored fragment covers are stored, so the stored
    /// fragments stay pairwise disjoint. Differing bytes in the covered part are reported as an overlap conflict.
    /// Deterministic and independent of any later arrival, which is what a replay-stable protocol needs.
    /// </summary>
    KeepFirst,
}

/// <summary>Which open datagram is evicted when a count or byte limit is reached.</summary>
public enum FragmentEvictionPolicy : byte
{
    /// <summary>The datagram whose first fragment arrived earliest, whatever happened to it since.</summary>
    OldestCreated,

    /// <summary>The datagram that received a fragment least recently, so an active long transfer survives a burst of short ones.</summary>
    LeastRecentlyUsed,
}

/// <summary>What the reassembler reports when the last fragment has arrived but a hole remains.</summary>
public enum FragmentGapPolicy : byte
{
    /// <summary>Report <see cref="FragmentOutcomeKind.Pending"/> until the datagram is complete or evicted.</summary>
    WaitForCompletion,

    /// <summary>
    /// Report the decodable prefix once, on the call that fixed the total length (<see cref="FragmentOutcomeKind.PartialPrefix"/>),
    /// keep the datagram open, and report <see cref="FragmentOutcomeKind.Complete"/> when the last hole closes.
    /// </summary>
    PrefixOnLastThenComplete,
}

/// <summary>
/// The limits and policies of one <see cref="FragmentReassembler{TKey}"/>. The owner of the reassembler picks the values;
/// a preset reproduces a protocol that already exists.
/// <para>
/// Every limit here is a trust-boundary bound: a hostile capture can open as many datagrams, send as many fragments and
/// claim as large an extent as the values allow. Each property documents the threat, whether the value is a constant of the
/// protocol or a choice of the owner, and its posture. The values are not end-user settings of this type; an owner that
/// exposes them (for example SOME/IP-TP through its settings) validates the user value with <see cref="TryValidate"/> and
/// names the setting in its own message.
/// </para>
/// <para><b>Thread-safety:</b> an immutable value; safe to share.</para>
/// </summary>
public readonly record struct FragmentReassemblyOptions
{
    /// <summary>Value of <see cref="MaxFragments"/> for "no separate fragment bound".</summary>
    public const int Unbounded = int.MaxValue;

    /// <summary>
    /// Maximum number of datagrams that are open at the same time. Must be greater than 0.
    /// <para>
    /// Threat: datagrams whose remaining fragments never arrive hold pooled copies of their fragments forever.
    /// Configurable by the owner (code or its settings), never by the capture. Posture: secure by default, the presets are
    /// finite and loosening is an explicit choice. At the limit the datagram chosen by <see cref="EvictionPolicy"/> is
    /// evicted and counted.
    /// </para>
    /// </summary>
    public int MaxPending { get; init; }

    /// <summary>
    /// Maximum number of stored fragment bytes over all open datagrams together; <see cref="long.MaxValue"/> means
    /// unbounded. Must be greater than 0.
    /// <para>
    /// Threat: the per-datagram length and the datagram count each look small, but they multiply (1,024 datagrams of 10 MiB
    /// are 10 GiB). Configurable by the owner. Posture: the IP presets are open by default (unbounded, unchanged behavior; the
    /// residual risk is bounded by <see cref="MaxPending"/> times <see cref="MaxFragments"/> fragments), other owners choose
    /// a finite value. The counter measures stored payload bytes; the pooled arrays behind them can be up to twice as large.
    /// </para>
    /// </summary>
    public long MaxBufferedBytes { get; init; }

    /// <summary>
    /// Maximum datagram length in bytes, measured as the extent of a fragment (offset plus length), so a single fragment at a
    /// huge offset cannot request a huge buffer on completion. Range 1 to <see cref="Array.MaxLength"/>.
    /// <para>
    /// Threat: memory exhaustion by one claimed extent. A constant for the IP presets (the 16-bit length of IP); a setting
    /// for owners whose format allows larger messages. Posture: secure by default.
    /// </para>
    /// </summary>
    public int MaxDatagramLength { get; init; }

    /// <summary>
    /// Maximum number of stored fragments per datagram; <see cref="Unbounded"/> for no separate bound. Must be greater than 0.
    /// <para>
    /// Threat: every insert scans and shifts the sorted fragment list, so an unbounded list makes each fragment cost more.
    /// A constant for IP (65,536 / 8, fragments sit at multiples of eight bytes); owners whose alignment already bounds the
    /// list through <see cref="MaxDatagramLength"/> pass <see cref="Unbounded"/>.
    /// </para>
    /// </summary>
    public int MaxFragments { get; init; }

    /// <summary>
    /// Every fragment but the last must carry a non-zero multiple of this many bytes; 1 means no rule. Must be at least 1.
    /// A format fact (for example SOME/IP-TP counts offsets in 16-byte units), never a limit.
    /// </summary>
    public int NonLastAlignment { get; init; }

    /// <summary>Handling of fragments that cover bytes another fragment already covers.</summary>
    public FragmentOverlapPolicy OverlapPolicy { get; init; }

    /// <summary>Which datagram is evicted at a count or byte limit.</summary>
    public FragmentEvictionPolicy EvictionPolicy { get; init; }

    /// <summary>
    /// When true, a fragment at offset 0 for a key that already holds a fragment at offset 0 starts the datagram over (the stored
    /// fragments are released and <see cref="FragmentOutcomeFlags.Restarted"/> is reported). A sender that restarts a transfer
    /// begins at offset 0 again, and the stale fragments belong to the abandoned attempt. A fragment at offset 0 that arrives
    /// behind later fragments of an out-of-order transfer is the missing start and does not restart anything.
    /// </summary>
    public bool RestartOnZeroOffset { get; init; }

    /// <summary>What to report when the last fragment arrived and a hole remains.</summary>
    public FragmentGapPolicy GapPolicy { get; init; }

    /// <summary>
    /// IPv4 reassembly exactly as it behaved before the generic type existed: 1,024 open datagrams, 65,535 bytes, 8,192
    /// fragments, largest duplicate wins, oldest created is evicted first, no byte budget (tightening it belongs to the
    /// limits concept, not to a refactoring).
    /// </summary>
    public static FragmentReassemblyOptions IPv4 { get; } = new()
    {
        MaxPending = 1024,
        MaxBufferedBytes = long.MaxValue,
        MaxDatagramLength = 65535,
        MaxFragments = 8192,
        NonLastAlignment = 1,
        OverlapPolicy = FragmentOverlapPolicy.KeepLargestDuplicate,
        EvictionPolicy = FragmentEvictionPolicy.OldestCreated,
        RestartOnZeroOffset = false,
        GapPolicy = FragmentGapPolicy.WaitForCompletion,
    };

    /// <summary>IPv6 reassembly exactly as before: the IPv4 values with the RFC 5722 rule that an overlap discards the datagram.</summary>
    public static FragmentReassemblyOptions IPv6 { get; } = IPv4 with { OverlapPolicy = FragmentOverlapPolicy.DiscardDatagram };

    /// <summary>Checks every value; the problem names the offending option and its allowed range.</summary>
    /// <param name="problem">The first problem found, or <see langword="null"/> when the options are valid.</param>
    /// <returns><see langword="true"/> when the options are valid.</returns>
    public bool TryValidate([NotNullWhen(false)] out string? problem)
    {
        // Each rule names its option, so an owner can show the message next to the setting it derived the value from.
        if (MaxPending <= 0)
        {
            problem = "MaxPending must be greater than 0.";
            return false;
        }

        if (MaxBufferedBytes <= 0)
        {
            problem = "MaxBufferedBytes must be greater than 0.";
            return false;
        }

        if (MaxDatagramLength < 1 || MaxDatagramLength > Array.MaxLength)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"MaxDatagramLength must be between 1 and {Array.MaxLength}.");
            return false;
        }

        if (MaxFragments <= 0)
        {
            problem = "MaxFragments must be greater than 0.";
            return false;
        }

        if (NonLastAlignment < 1)
        {
            problem = "NonLastAlignment must be at least 1.";
            return false;
        }

        // An enum can hold any byte value; an undefined policy would silently take the default branch of a switch.
        if (!Enum.IsDefined(OverlapPolicy))
        {
            problem = "OverlapPolicy is not a defined value.";
            return false;
        }

        if (!Enum.IsDefined(EvictionPolicy))
        {
            problem = "EvictionPolicy is not a defined value.";
            return false;
        }

        if (!Enum.IsDefined(GapPolicy))
        {
            problem = "GapPolicy is not a defined value.";
            return false;
        }

        problem = null;
        return true;
    }
}
