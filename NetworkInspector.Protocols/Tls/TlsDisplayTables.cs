// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Protocols.Tls;

/// <summary>
/// Precomputed display text tables for TLS protocol fields.
/// Zero-allocation lookups via array indexing and binary search.
/// </summary>
internal static class TlsDisplayTables
{
    #region Content Type (5 known values for byte field)

    private static readonly string[] ContentTypeTable = BuildContentTypeTable();

    /// <summary>Returns display text for a TLS content type byte.</summary>
    internal static string GetContentTypeDisplayText(byte ct) => ContentTypeTable[ct];

    private static string[] BuildContentTypeTable()
    {
        string[] table = new string[256];
        table[20] = "Change Cipher Spec (20)";
        table[21] = "Alert (21)";
        table[22] = "Handshake (22)";
        table[23] = "Application Data (23)";
        table[25] = "Heartbeat (25)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString();
        }
        return table;
    }

    /// <summary>Returns short name for a TLS content type.</summary>
    internal static string GetContentTypeName(byte ct) => ct switch
    {
        20 => "Change Cipher Spec",
        21 => "Alert",
        22 => "Handshake",
        23 => "Application Data",
        25 => "Heartbeat",
        _ => ct.ToString()
    };

    #endregion

    #region TLS Version (sparse — use dictionary)

    private static readonly Dictionary<ushort, string> VersionDisplayTexts = new()
    {
        [0x0300] = "SSL 3.0 (0x0300)",
        [0x0301] = "TLS 1.0 (0x0301)",
        [0x0302] = "TLS 1.1 (0x0302)",
        [0x0303] = "TLS 1.2 (0x0303)",
        [0x0304] = "TLS 1.3 (0x0304)",
    };

    /// <summary>Returns display text for a TLS version field.</summary>
    internal static string GetVersionDisplayText(ushort version) =>
        VersionDisplayTexts.TryGetValue(version, out string? text) ? text : $"0x{version:X4}";

    #endregion

    #region Handshake Type (byte field)

    private static readonly string[] HandshakeTypeTable = BuildHandshakeTypeTable();

    /// <summary>Returns display text for a TLS handshake type byte.</summary>
    internal static string GetHandshakeTypeDisplayText(byte hsType) => HandshakeTypeTable[hsType];

    /// <summary>Returns short name for a handshake type.</summary>
    internal static string GetHandshakeTypeName(byte hsType) => hsType switch
    {
        0 => "Hello Request",
        1 => "Client Hello",
        2 => "Server Hello",
        4 => "New Session Ticket",
        5 => "End of Early Data",
        8 => "Encrypted Extensions",
        11 => "Certificate",
        12 => "Server Key Exchange",
        13 => "Certificate Request",
        14 => "Server Hello Done",
        15 => "Certificate Verify",
        16 => "Client Key Exchange",
        20 => "Finished",
        _ => hsType.ToString()
    };

    private static string[] BuildHandshakeTypeTable()
    {
        string[] table = new string[256];
        table[0] = "Hello Request (0)";
        table[1] = "Client Hello (1)";
        table[2] = "Server Hello (2)";
        table[4] = "New Session Ticket (4)";
        table[5] = "End of Early Data (5)";
        table[8] = "Encrypted Extensions (8)";
        table[11] = "Certificate (11)";
        table[12] = "Server Key Exchange (12)";
        table[13] = "Certificate Request (13)";
        table[14] = "Server Hello Done (14)";
        table[15] = "Certificate Verify (15)";
        table[16] = "Client Key Exchange (16)";
        table[20] = "Finished (20)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString();
        }
        return table;
    }

    #endregion

    #region Cipher Suites (binary search on sorted list)

    /// <summary>Known cipher suite entries sorted by code for binary search.</summary>
    private static readonly (ushort Code, string Name)[] CipherSuites = BuildCipherSuiteTable();

    /// <summary>Precomputed display text for GREASE values (0x?A?A pattern, 16 entries).</summary>
    private static readonly Dictionary<ushort, string> GreaseDisplayTexts = BuildGreaseDisplayTexts();

    /// <summary>Builds the 16-entry GREASE display text lookup.</summary>
    private static Dictionary<ushort, string> BuildGreaseDisplayTexts()
    {
        Dictionary<ushort, string> result = new(16);
        for (int hi = 0; hi < 16; hi++)
        {
            // GREASE pattern: high nibble in both bytes, 0x0A in low nibble of both bytes
            ushort code = (ushort)((hi << 12) | (0x0A << 8) | (hi << 4) | 0x0A);
            result[code] = $"GREASE (0x{code:X4})";
        }
        return result;
    }

    /// <summary>Returns display text for a TLS cipher suite code.</summary>
    internal static string GetCipherSuiteDisplayText(ushort code)
    {
        // GREASE detection: pattern 0x?A?A — precomputed lookup
        if ((code & 0x0F0F) == 0x0A0A)
        {
            return GreaseDisplayTexts[code];
        }

        int idx = BinarySearchCipherSuite(code);
        if (idx >= 0)
        {
            return CipherSuites[idx].Name;
        }
        return $"Unknown (0x{code:X4})";
    }

    private static int BinarySearchCipherSuite(ushort code)
    {
        int lo = 0;
        int hi = CipherSuites.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ushort midCode = CipherSuites[mid].Code;
            if (midCode == code)
            {
                return mid;
            }
            if (midCode < code)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return -1;
    }

    private static (ushort, string)[] BuildCipherSuiteTable()
    {
        // Sorted by code for binary search. Includes the most common suites.
        (ushort, string)[] table =
        [
            (0x0000, "TLS_NULL_WITH_NULL_NULL (0x0000)"),
            (0x002F, "TLS_RSA_WITH_AES_128_CBC_SHA (0x002F)"),
            (0x0033, "TLS_DHE_RSA_WITH_AES_128_CBC_SHA (0x0033)"),
            (0x0035, "TLS_RSA_WITH_AES_256_CBC_SHA (0x0035)"),
            (0x0039, "TLS_DHE_RSA_WITH_AES_256_CBC_SHA (0x0039)"),
            (0x003C, "TLS_RSA_WITH_AES_128_CBC_SHA256 (0x003C)"),
            (0x003D, "TLS_RSA_WITH_AES_256_CBC_SHA256 (0x003D)"),
            (0x0067, "TLS_DHE_RSA_WITH_AES_128_CBC_SHA256 (0x0067)"),
            (0x006B, "TLS_DHE_RSA_WITH_AES_256_CBC_SHA256 (0x006B)"),
            (0x009C, "TLS_RSA_WITH_AES_128_GCM_SHA256 (0x009C)"),
            (0x009D, "TLS_RSA_WITH_AES_256_GCM_SHA384 (0x009D)"),
            (0x009E, "TLS_DHE_RSA_WITH_AES_128_GCM_SHA256 (0x009E)"),
            (0x009F, "TLS_DHE_RSA_WITH_AES_256_GCM_SHA384 (0x009F)"),
            (0x00FF, "TLS_EMPTY_RENEGOTIATION_INFO_SCSV (0x00FF)"),
            (0x1301, "TLS_AES_128_GCM_SHA256 (0x1301)"),
            (0x1302, "TLS_AES_256_GCM_SHA384 (0x1302)"),
            (0x1303, "TLS_CHACHA20_POLY1305_SHA256 (0x1303)"),
            (0x5600, "TLS_FALLBACK_SCSV (0x5600)"),
            (0xC009, "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA (0xC009)"),
            (0xC00A, "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA (0xC00A)"),
            (0xC013, "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA (0xC013)"),
            (0xC014, "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA (0xC014)"),
            (0xC023, "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256 (0xC023)"),
            (0xC024, "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384 (0xC024)"),
            (0xC027, "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256 (0xC027)"),
            (0xC028, "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384 (0xC028)"),
            (0xC02B, "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256 (0xC02B)"),
            (0xC02C, "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384 (0xC02C)"),
            (0xC02F, "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 (0xC02F)"),
            (0xC030, "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384 (0xC030)"),
            (0xCCA8, "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256 (0xCCA8)"),
            (0xCCA9, "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256 (0xCCA9)"),
            (0xCCAA, "TLS_DHE_RSA_WITH_CHACHA20_POLY1305_SHA256 (0xCCAA)"),
        ];
        return table;
    }

    #endregion

    #region Extension Types

    private static readonly Dictionary<ushort, string> ExtensionTypeNames = new()
    {
        [0] = "server_name",
        [1] = "max_fragment_length",
        [5] = "status_request",
        [10] = "supported_groups",
        [11] = "ec_point_formats",
        [13] = "signature_algorithms",
        [14] = "use_srtp",
        [15] = "heartbeat",
        [16] = "application_layer_protocol_negotiation",
        [18] = "signed_certificate_timestamp",
        [21] = "padding",
        [23] = "extended_master_secret",
        [27] = "compress_certificate",
        [35] = "session_ticket",
        [41] = "pre_shared_key",
        [42] = "early_data",
        [43] = "supported_versions",
        [44] = "cookie",
        [45] = "psk_key_exchange_modes",
        [47] = "certificate_authorities",
        [49] = "post_handshake_auth",
        [50] = "signature_algorithms_cert",
        [51] = "key_share",
        [0xFF01] = "renegotiation_info",
    };

    /// <summary>Precomputed display text for known TLS extension types.</summary>
    private static readonly Dictionary<ushort, string> ExtensionTypeDisplayTexts = BuildExtensionTypeDisplayTexts();

    /// <summary>Builds the precomputed extension type display text dictionary.</summary>
    private static Dictionary<ushort, string> BuildExtensionTypeDisplayTexts()
    {
        Dictionary<ushort, string> result = new(ExtensionTypeNames.Count);
        foreach ((ushort type, string name) in ExtensionTypeNames)
        {
            result[type] = $"{name} ({type})";
        }
        return result;
    }

    /// <summary>Returns display text for a TLS extension type.</summary>
    internal static string GetExtensionTypeDisplayText(ushort type)
    {
        // GREASE detection — precomputed lookup (shared with cipher suites)
        if ((type & 0x0F0F) == 0x0A0A)
        {
            return GreaseDisplayTexts[type];
        }

        if (ExtensionTypeDisplayTexts.TryGetValue(type, out string? text))
        {
            return text;
        }
        return $"Unknown ({type})";
    }

    /// <summary>Returns the short name for a TLS extension type.</summary>
    internal static string GetExtensionTypeName(ushort type)
    {
        if ((type & 0x0F0F) == 0x0A0A)
        {
            return "GREASE";
        }
        return ExtensionTypeNames.TryGetValue(type, out string? name) ? name : type.ToString();
    }

    #endregion

    #region Alert Level / Description

    /// <summary>Returns display text for a TLS alert level.</summary>
    internal static string GetAlertLevelDisplayText(byte level) => level switch
    {
        1 => "Warning (1)",
        2 => "Fatal (2)",
        _ => level.ToString()
    };

    /// <summary>Returns display text for a TLS alert description.</summary>
    internal static string GetAlertDescriptionDisplayText(byte desc) => desc switch
    {
        0 => "Close Notify (0)",
        10 => "Unexpected Message (10)",
        20 => "Bad Record MAC (20)",
        21 => "Decryption Failed (21)",
        22 => "Record Overflow (22)",
        30 => "Decompression Failure (30)",
        40 => "Handshake Failure (40)",
        42 => "Bad Certificate (42)",
        43 => "Unsupported Certificate (43)",
        44 => "Certificate Revoked (44)",
        45 => "Certificate Expired (45)",
        46 => "Certificate Unknown (46)",
        47 => "Illegal Parameter (47)",
        48 => "Unknown CA (48)",
        49 => "Access Denied (49)",
        50 => "Decode Error (50)",
        51 => "Decrypt Error (51)",
        70 => "Protocol Version (70)",
        71 => "Insufficient Security (71)",
        80 => "Internal Error (80)",
        86 => "Inappropriate Fallback (86)",
        90 => "User Canceled (90)",
        100 => "No Renegotiation (100)",
        109 => "Missing Extension (109)",
        110 => "Unsupported Extension (110)",
        112 => "Unrecognized Name (112)",
        113 => "Bad Certificate Status Response (113)",
        115 => "Unknown PSK Identity (115)",
        116 => "Certificate Required (116)",
        120 => "No Application Protocol (120)",
        _ => desc.ToString()
    };

    #endregion

    #region Compression Methods

    /// <summary>Precomputed display text table for TLS compression methods (256 entries).</summary>
    private static readonly string[] CompressionMethodTable = BuildCompressionMethodTable();

    /// <summary>Returns display text for a TLS compression method byte.</summary>
    internal static string GetCompressionMethodDisplayText(byte method) =>
        CompressionMethodTable[method];

    private static string[] BuildCompressionMethodTable()
    {
        string[] table = new string[256];
        table[0] = "null (0)";
        table[1] = "DEFLATE (1)";
        table[2] = "LZS (2)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString();
        }
        return table;
    }

    #endregion

    #region Supported Groups (elliptic curves / named groups, RFC 8422/8446)

    private static readonly Dictionary<ushort, string> SupportedGroupNames = new()
    {
        [1] = "sect163k1",
        [19] = "secp192r1",
        [21] = "secp224r1",
        [23] = "secp256r1",
        [24] = "secp384r1",
        [25] = "secp521r1",
        [29] = "x25519",
        [30] = "x448",
        [256] = "ffdhe2048",
        [257] = "ffdhe3072",
        [258] = "ffdhe4096",
        [259] = "ffdhe6144",
        [260] = "ffdhe8192",
    };

    /// <summary>Returns display text for a TLS supported group.</summary>
    internal static string GetSupportedGroupDisplayText(ushort group)
    {
        if ((group & 0x0F0F) == 0x0A0A)
        {
            return GreaseDisplayTexts.TryGetValue(group, out string? grease) ? grease : $"GREASE (0x{group:X4})";
        }
        return SupportedGroupNames.TryGetValue(group, out string? name) ? $"{name} ({group})" : $"Unknown ({group})";
    }

    #endregion

    #region Signature Algorithms (RFC 8446 Section 4.2.3)

    private static readonly Dictionary<ushort, string> SignatureAlgorithmNames = new()
    {
        [0x0201] = "rsa_pkcs1_sha1",
        [0x0301] = "SHA224 ECDSA",
        [0x0401] = "rsa_pkcs1_sha256",
        [0x0403] = "ecdsa_secp256r1_sha256",
        [0x0501] = "rsa_pkcs1_sha384",
        [0x0503] = "ecdsa_secp384r1_sha384",
        [0x0601] = "rsa_pkcs1_sha512",
        [0x0603] = "ecdsa_secp521r1_sha512",
        [0x0804] = "rsa_pss_rsae_sha256",
        [0x0805] = "rsa_pss_rsae_sha384",
        [0x0806] = "rsa_pss_rsae_sha512",
        [0x0807] = "ed25519",
        [0x0808] = "ed448",
        [0x0809] = "rsa_pss_pss_sha256",
        [0x080A] = "rsa_pss_pss_sha384",
        [0x080B] = "rsa_pss_pss_sha512",
    };

    /// <summary>Returns display text for a TLS signature algorithm.</summary>
    internal static string GetSignatureAlgorithmDisplayText(ushort algo)
    {
        if ((algo & 0x0F0F) == 0x0A0A)
        {
            return GreaseDisplayTexts.TryGetValue(algo, out string? grease) ? grease : $"GREASE (0x{algo:X4})";
        }
        return SignatureAlgorithmNames.TryGetValue(algo, out string? name)
            ? $"{name} (0x{algo:X4})"
            : $"Unknown (0x{algo:X4})";
    }
    #endregion
}
