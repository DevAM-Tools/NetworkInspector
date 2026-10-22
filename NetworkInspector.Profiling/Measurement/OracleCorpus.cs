// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Filter expressions of the correctness oracle. Every language feature appears at least once, so a backend or
/// evaluator change that alters any verdict shows up as a byte difference. All names exist in
/// <see cref="StackHelper.CreateFullStack"/>.
/// <para><b>Thread-safety:</b> immutable.</para>
/// </summary>
internal static class OracleCorpus
{
    #region Expressions

    /// <summary>
    /// Field-tree filter expressions: protocol, field, and alias presence; every comparison operator; sets; ranges;
    /// slices; <c>len()</c>; <c>contains</c>; <c>matches</c> (linear and backtracking engine); negation; constants;
    /// nesting; scopes with and without occurrence, nested; and every flank form (arrival, departure, changed, by,
    /// armed from+to, armed from+by, packet window, time window, when gate).
    /// </summary>
    internal static readonly string[] FilterExpressions =
    [
        "udp",
        "tcp",
        "ipv6 && !ip",
        "udp.srcport",
        "udp.port",
        "udp.srcport == 53",
        "udp.srcport != 53",
        "udp.dstport < 1024",
        "udp.dstport <= 1024",
        "tcp.srcport > 49151",
        "tcp.srcport >= 49152",
        "udp.port in {53, 123, 5353}",
        "tcp.port in 1024..2048",
        "eth.src == 66:77:88:99:aa:bb || eth.dst[0:3] == 00:11:22",
        "ip.src == 10.0.0.1 || ip.ttl < 64",
        "ipv6.hlim >= 64 && udp",
        "len(udp.payload) > 100",
        "frame.len > 1000 || frame.len < 64",
        "tcp.flags.syn == 1",
        "tcp.flags.syn == true && !(tcp.srcport == 80)",
        "dns.qry.name contains \"example\"",
        "dns.qry.name matches \"^[a-z]+\\\\.com$\"",
        "dns.qry.name matches \"(?=.*a)\"",
        "true",
        "false || udp",
        "$udp { udp.srcport == 53 }",
        "$udp[1] { udp.dstport > 0 }",
        "$ipv6 { $udp[0] { udp.srcport < 1024 } }",
        "flank(udp.srcport, to: >= 1024, within: 5packets)",
        "flank(udp.srcport, from: < 1024, within: 5packets)",
        "flank(tcp.srcport, changed, within: 1s)",
        "flank(udp.srcport, by: > 100, within: 10packets)",
        "flank(udp.dstport, from: < 1024, to: >= 1024, within: 10packets)",
        "flank(udp.srcport, from: < 1024, by: > 1000, within: 2s)",
        "udp && flank(udp.srcport, changed, within: 4packets, when: udp.dstport > 1024)",
        "eth.type == 2048 || ipv6.hlim == 1 || udp.dstport == 1 || eth.dst == 00:00:00:00:00:01",
    ];

    /// <summary>Tree-free observer expressions (no scopes, at most one flank).</summary>
    internal static readonly string[] ObserverExpressions =
    [
        "udp",
        "tcp",
        "udp.srcport == 53",
        "udp.dstport < 1024",
        "udp.port in {53, 123, 5353}",
        "tcp.port in 1024..2048",
        "eth.src == 66:77:88:99:aa:bb || eth.dst[0:3] == 00:11:22",
        "len(udp.payload) > 100",
        "tcp.flags.syn == 1",
        "dns.qry.name contains \"example\"",
        "dns.qry.name matches \"^[a-z]+\\\\.com$\"",
        "flank(udp.srcport, to: >= 1024, within: 5packets)",
        "flank(tcp.srcport, changed, within: 1s)",
        "flank(udp.dstport, from: < 1024, to: >= 1024, within: 10packets)",
        "udp && flank(udp.srcport, changed, within: 4packets, when: udp.dstport > 1024)",
        "eth.type == 2048 || ipv6.hlim == 1 || udp.dstport == 1 || eth.dst == 00:00:00:00:00:01",
    ];

    /// <summary>
    /// Listener filters of the session digest: three stateless (one matching about half of the UDP packets, so match
    /// and non-match paths both run), one scoped, one stateful.
    /// </summary>
    internal static readonly string[] SessionExpressions =
    [
        "udp.srcport == 53 || udp.dstport == 53",
        "udp.srcport < 32768",
        "tcp && tcp.flags.syn == 1",
        "$ipv6[0] { ipv6.hlim < 100 }",
        "flank(udp.srcport, changed, within: 3packets)",
    ];

    #endregion
}
