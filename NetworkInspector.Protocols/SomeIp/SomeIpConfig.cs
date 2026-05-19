// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Configuration model for SOME/IP protocol. Provides service and method
/// name resolution from a JSON config file.
/// </summary>
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
/// of SOME/IP configuration files.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SomeIpConfig))]
internal sealed partial class SomeIpConfigContext : JsonSerializerContext;
