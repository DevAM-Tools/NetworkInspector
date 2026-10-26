// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Configuration model for SOME/IP protocol. Provides service and method
/// name resolution from a JSON config file (setting <c>someip.config_file</c>).
/// </summary>
/// <remarks>
/// Pure DTOs for untrusted, user-supplied JSON: no validation here. <c>SomeIpProtocol</c> copies them into frozen
/// lookup tables once at registration; the DTOs are not used while parsing. If the same service id or message id
/// (service id and method id) is defined more than once, the first definition wins and later ones are ignored
/// without a warning. Ids are plain JSON numbers (decimal), limited to the 16-bit range by the property type, so
/// out-of-range values are rejected by the JSON deserializer.
/// </remarks>
internal sealed class SomeIpConfig
{
    /// <summary>List of service definitions with method name mappings.</summary>
    [JsonPropertyName("services")]
    public SomeIpServiceEntry[] Services { get; set; } = [];
}

/// <summary>
/// A single SOME/IP service entry with its methods.
/// </summary>
internal sealed class SomeIpServiceEntry
{
    /// <summary>16-bit service ID.</summary>
    [JsonPropertyName("service_id")]
    public ushort ServiceId
    {
        get; set;
    }

    /// <summary>Human-readable service name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>List of method definitions for this service.</summary>
    [JsonPropertyName("methods")]
    public SomeIpMethodEntry[] Methods { get; set; } = [];
}

/// <summary>
/// A single SOME/IP method entry.
/// </summary>
internal sealed class SomeIpMethodEntry
{
    /// <summary>16-bit method ID.</summary>
    [JsonPropertyName("method_id")]
    public ushort MethodId
    {
        get; set;
    }

    /// <summary>Human-readable method name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Source-generated JSON serialization context for AOT-safe deserialization
/// of SOME/IP configuration files (no reflection at runtime).
/// The options are deliberately lenient because the file is hand-edited: property names match case-insensitively,
/// comments are skipped, and trailing commas are allowed.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SomeIpConfig))]
internal sealed partial class SomeIpConfigContext : JsonSerializerContext;
