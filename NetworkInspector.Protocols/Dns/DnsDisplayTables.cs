// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Dns;

/// <summary>
/// Precomputed display text tables for DNS field values.
/// Provides zero-allocation lookups for common DNS types, classes, opcodes, and rcodes.
/// </summary>
internal static class DnsDisplayTables
{
    #region DNS Record Types (QType)
    // Only the most common types have named entries; the rest show just the number.
    // Max known type code is 257 (CAA) — a 258-entry array covers all defined types.

    // Private holder built once; both public arrays reference its fields.
    private static readonly (string?[] _TypeNames, string[] DisplayTexts) _TypeData = _BuildTypeData();

    /// <summary>
    /// Short name table for DNS record types (index = type code, null = unknown/unassigned).
    /// Codes above 257 fall back to numeric strings on range check.
    /// </summary>
    private static readonly string?[] _TypeNames = _TypeData._TypeNames;

    /// <summary>
    /// Display text table for DNS record types (e.g. "A (1)") indexed by type code.
    /// Unknown entries contain the numeric string (e.g. "30").
    /// </summary>
    private static readonly string[] _TypeDisplayTexts = _TypeData.DisplayTexts;

    /// <summary>Builds the 258-entry type name and display text arrays in a single pass.</summary>
    private static (string?[] _TypeNames, string[] DisplayTexts) _BuildTypeData()
    {
        string?[] names = new string?[258]; // covers type codes 0–257
        string[] displayTexts = new string[258];

        // Pre-fill display texts with the numeric string as fallback for all entries.
        for (int i = 0; i < displayTexts.Length; i++)
        {
            displayTexts[i] = i.ToString(CultureInfo.InvariantCulture);
        }

        // Known DNS type codes — RFC 1035, 2535, 2782, 3596, 6698, 7208, 8659, etc.
        (ushort Code, string Name)[] knownTypes =
        [
            (1,   "A"),          (2,   "NS"),         (3,   "MD"),
            (4,   "MF"),         (5,   "CNAME"),       (6,   "SOA"),
            (7,   "MB"),         (8,   "MG"),          (9,   "MR"),
            (10,  "NULL"),       (11,  "WKS"),         (12,  "PTR"),
            (13,  "HINFO"),      (14,  "MINFO"),       (15,  "MX"),
            (16,  "TXT"),        (17,  "RP"),          (18,  "AFSDB"),
            (19,  "X25"),        (20,  "ISDN"),        (21,  "RT"),
            (22,  "NSAP"),       (23,  "NSAP-PTR"),    (24,  "SIG"),
            (25,  "KEY"),        (26,  "PX"),          (27,  "GPOS"),
            (28,  "AAAA"),       (29,  "LOC"),         (33,  "SRV"),
            (35,  "NAPTR"),      (36,  "KX"),          (37,  "CERT"),
            (39,  "DNAME"),      (41,  "OPT"),         (43,  "DS"),
            (44,  "SSHFP"),      (45,  "IPSECKEY"),    (46,  "RRSIG"),
            (47,  "NSEC"),       (48,  "DNSKEY"),      (49,  "DHCID"),
            (50,  "NSEC3"),      (51,  "NSEC3PARAM"),  (52,  "TLSA"),
            (55,  "HIP"),        (59,  "CDS"),         (60,  "CDNSKEY"),
            (61,  "OPENPGPKEY"), (64,  "SVCB"),        (65,  "HTTPS"),
            (99,  "SPF"),        (249, "TKEY"),         (250, "TSIG"),
            (251, "IXFR"),       (252, "AXFR"),         (255, "ANY"),
            (256, "URI"),        (257, "CAA"),
        ];

        foreach ((ushort code, string name) in knownTypes)
        {
            names[code] = name;
            displayTexts[code] = $"{name} ({code})";
        }

        return (names, displayTexts);
    }

    /// <summary>Gets display text for a DNS record type (e.g. "A (1)").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string GetTypeDisplayText(ushort qtype)
    {
        if (qtype < _TypeDisplayTexts.Length)
        {
            return _TypeDisplayTexts[qtype];
        }

        return qtype.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Gets the short name for a DNS record type (e.g. "A", "AAAA").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string GetTypeName(ushort qtype)
    {
        if (qtype < _TypeNames.Length)
        {
            string? name = _TypeNames[qtype];
            if (name is not null)
            {
                return name;
            }

            return qtype.ToString(CultureInfo.InvariantCulture);
        }

        return qtype.ToString(CultureInfo.InvariantCulture);
    }

    #endregion

    #region DNS Classes

    /// <summary>Precomputed 8-entry display text table for DNS query classes.</summary>
    private static readonly string[] _ClassDisplayTexts =
    [
        "Reserved (0)",   // 0
        "IN (1)",         // 1
        "CS (2)",         // 2
        "CH (3)",         // 3
        "HS (4)",         // 4
    ];

    /// <summary>Gets display text for a DNS class value (e.g. "IN (1)").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string GetClassDisplayText(ushort qclass)
    {
        if (qclass < _ClassDisplayTexts.Length)
        {
            return _ClassDisplayTexts[qclass];
        }
        if (qclass == 255)
        {
            return "ANY (255)";
        }

        return qclass.ToString(CultureInfo.InvariantCulture);
    }

    #endregion

    #region DNS Opcodes

    /// <summary>Precomputed 16-entry display text table for DNS opcodes.</summary>
    private static readonly string[] _OpcodeDisplayTexts =
    [
        "Standard query (0)",    // 0
        "Inverse query (1)",     // 1
        "Server status (2)",     // 2
        "3",                     // 3 (reserved)
        "Notify (4)",            // 4
        "Update (5)",            // 5
        "DNS Stateful Ops (6)",  // 6
        "7", "8", "9", "10", "11", "12", "13", "14", "15",
    ];

    /// <summary>Gets display text for a DNS opcode (e.g. "Standard query (0)").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string GetOpcodeDisplayText(byte opcode)
    {
        if (opcode < 16)
        {
            return _OpcodeDisplayTexts[opcode];
        }

        return opcode.ToString(CultureInfo.InvariantCulture);
    }

    #endregion

    #region DNS Response Codes

    /// <summary>Precomputed 16-entry display text table for DNS response codes.</summary>
    private static readonly string[] _RcodeDisplayTexts =
    [
        "No error (0)",       // 0
        "Format error (1)",   // 1
        "Server failure (2)", // 2
        "Name error (3)",     // 3 (NXDOMAIN)
        "Not implemented (4)", // 4
        "Refused (5)",        // 5
        "YX Domain (6)",      // 6
        "YX RR Set (7)",      // 7
        "NX RR Set (8)",      // 8
        "Not Auth (9)",       // 9
        "Not Zone (10)",      // 10
        "DSOTYPENI (11)",     // 11
        "12", "13", "14", "15",
    ];

    /// <summary>Gets display text for a DNS response code (e.g. "No error (0)").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string GetRcodeDisplayText(byte rcode)
    {
        if (rcode < 16)
        {
            return _RcodeDisplayTexts[rcode];
        }

        return rcode.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Gets the display name for an EDNS0 option code (RFC 6891 / IANA registry).</summary>
    internal static string GetEdnsOptionName(ushort code) => code switch
    {
        0 => "Reserved",
        1 => "LLQ",
        2 => "UL",
        3 => "NSID",
        5 => "DAU",
        6 => "DHU",
        7 => "N3U",
        8 => "edns-client-subnet",
        9 => "EDNS EXPIRE",
        10 => "COOKIE",
        11 => "edns-tcp-keepalive",
        12 => "Padding",
        13 => "CHAIN",
        14 => "edns-key-tag",
        15 => "Extended DNS Error",
        _ => "Unknown",
    };

    #endregion

    #region DNSSEC Algorithm Numbers (RFC 4034, 5702, 6605, 8080, 8624)

    /// <summary>Gets display text for a DNSSEC algorithm number.</summary>
    internal static LazyString GetDnssecAlgorithmDisplayText(byte algorithm) => algorithm switch
    {
        0 => "Reserved (0)",
        1 => "RSA/MD5 (1)",
        3 => "DSA/SHA1 (3)",
        5 => "RSA/SHA-1 (5)",
        6 => "DSA-NSEC3-SHA1 (6)",
        7 => "RSASHA1-NSEC3-SHA1 (7)",
        8 => "RSA/SHA-256 (8)",
        10 => "RSA/SHA-512 (10)",
        12 => "GOST R 34.10-2001 (12)",
        13 => "ECDSA Curve P-256 with SHA-256 (13)",
        14 => "ECDSA Curve P-384 with SHA-384 (14)",
        15 => "Ed25519 (15)",
        16 => "Ed448 (16)",
        253 => "Private (253)",
        254 => "Private (254)",
        _ => ZA.Lazy("Algorithm ", algorithm)
    };

    #endregion

    #region DS Digest Types (RFC 4034, 4509, 5933, 6605)

    /// <summary>Gets display text for a DS record digest type.</summary>
    internal static LazyString GetDsDigestTypeDisplayText(byte digestType) => digestType switch
    {
        0 => "Reserved (0)",
        1 => "SHA-1 (1)",
        2 => "SHA-256 (2)",
        3 => "GOST R 34.11-94 (3)",
        4 => "SHA-384 (4)",
        _ => ZA.Lazy("Digest Type ", digestType)
    };
    #endregion
}
