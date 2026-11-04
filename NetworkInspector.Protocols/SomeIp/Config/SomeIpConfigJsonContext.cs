// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Config;

/// <summary>
/// Source-generated JSON context for the SOME/IP service description.
/// <para>
/// Strict on purpose: an unknown property is a defect the author must see, because a silently ignored typo looks like an
/// applied setting, so the options refuse unmapped members. Comments and trailing commas stay allowed because the file is
/// hand-edited. Property names are case-sensitive (the schema's names are the contract). Out-of-order metadata is allowed
/// so that <c>kind</c> and <c>category</c> need not be the first property of an object. A JSON <c>null</c> for a property or
/// element that the DTOs declare non-nullable is refused (<c>RespectNullableAnnotations</c>), so no null reaches the
/// compiler. Polymorphism is restricted to the
/// closed <see cref="JsonDerivedTypeAttribute"/> sets on the DTOs: no type name is ever taken from the input.
/// </para>
/// <para><b>Thread-safety:</b> immutable generated metadata; safe for concurrent use.</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    Converters = [typeof(SomeIpParameterListConverter), typeof(SomeIpUsageSiteConverter)])]
[JsonSerializable(typeof(SomeIpConfig))]
internal sealed partial class SomeIpConfigJsonContext : JsonSerializerContext;

/// <summary>
/// Reads a parameter list: a JSON array is a sequential list, a JSON object keyed by TLV id is a TLV list. The shape decides
/// the wire encoding, so no discriminator property is needed. Every other shape is a <see cref="JsonException"/> that the
/// loader turns into one warning with the path.
/// <para><b>Thread-safety:</b> stateless.</para>
/// </summary>
internal sealed class SomeIpParameterListConverter : JsonConverter<SomeIpParameterListConfig>
{
    /// <inheritdoc/>
    public override SomeIpParameterListConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonTypeInfo<SomeIpUsageSiteConfig> siteInfo = (JsonTypeInfo<SomeIpUsageSiteConfig>)options.GetTypeInfo(typeof(SomeIpUsageSiteConfig));

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            List<SomeIpUsageSiteConfig> sites = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                sites.Add(JsonSerializer.Deserialize(ref reader, siteInfo)
                    ?? throw new JsonException("A parameter must be a type name or an object, not null."));
            }

            return new SomeIpParameterListConfig { Sequential = [.. sites] };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Keys stay strings; the compiler validates the range (0..4095). A repeated key is a defect the author must see:
            // keeping the last one would silently drop the first definition.
            Dictionary<string, SomeIpUsageSiteConfig> members = new(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string key = reader.GetString()!; // PropertyName token: never null
                _ = reader.Read();
                SomeIpUsageSiteConfig site = JsonSerializer.Deserialize(ref reader, siteInfo)
                    ?? throw new JsonException("A TLV member must be a type name or an object, not null.");
                if (!members.TryAdd(key, site))
                {
                    throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"The TLV id '{key}' appears twice in one parameter list."));
                }
            }

            return new SomeIpParameterListConfig { Tlv = members };
        }

        throw new JsonException("A parameter list must be an array (sequential) or an object keyed by TLV id.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, SomeIpParameterListConfig value, JsonSerializerOptions options)
    {
        JsonTypeInfo<SomeIpUsageSiteConfig> siteInfo = (JsonTypeInfo<SomeIpUsageSiteConfig>)options.GetTypeInfo(typeof(SomeIpUsageSiteConfig));

        if (value.Tlv is not null)
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, SomeIpUsageSiteConfig> member in value.Tlv)
            {
                writer.WritePropertyName(member.Key);
                JsonSerializer.Serialize(writer, member.Value, siteInfo);
            }

            writer.WriteEndObject();
            return;
        }

        writer.WriteStartArray();
        foreach (SomeIpUsageSiteConfig site in value.Sequential ?? [])
        {
            JsonSerializer.Serialize(writer, site, siteInfo);
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// Reads a usage site: a bare string is the type reference, an object adds names and overrides. The object is read by hand
/// (not through the generated metadata of the same type, which would call this converter again) and refuses unknown properties.
/// <para><b>Thread-safety:</b> stateless.</para>
/// </summary>
internal sealed class SomeIpUsageSiteConverter : JsonConverter<SomeIpUsageSiteConfig>
{
    /// <inheritdoc/>
    public override SomeIpUsageSiteConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new SomeIpUsageSiteConfig { TypeReference = reader.GetString()! }; // String token: never null
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A parameter, member, or alternative must be a type name or an object.");
        }

        SomeIpUsageSiteConfig site = new();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string property = reader.GetString()!; // PropertyName token: never null
            _ = reader.Read();
            switch (property)
            {
                case "name":
                    site.Name = _ReadString(ref reader, property);
                    break;
                case "ui_name":
                    site.UiName = _ReadString(ref reader, property);
                    break;
                case "type_reference":
                    site.TypeReference = _ReadString(ref reader, property) ?? "";
                    break;
                case "pad_to":
                    site.PadTo = _ReadInt(ref reader, property);
                    break;
                default:
                    throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"The property '{property}' is not allowed here."));
            }
        }

        return site;
    }

    /// <summary>
    /// Reads a string or null. The reader's own accessors throw <see cref="InvalidOperationException"/> for another token type,
    /// which would escape the loader's handling of file content; a wrong type is a <see cref="JsonException"/> that names the
    /// property instead.
    /// </summary>
    private static string? _ReadString(ref Utf8JsonReader reader, string property)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"The property '{property}' must be a string."));
        }

        return reader.GetString();
    }

    /// <summary>Reads an integer or null; any other token, or a number that is not an int, is a <see cref="JsonException"/>.</summary>
    private static int? _ReadInt(ref Utf8JsonReader reader, string property)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
        {
            throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"The property '{property}' must be an integer."));
        }

        return value;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, SomeIpUsageSiteConfig value, JsonSerializerOptions options)
    {
        // A site that only names its type is written as the bare string, which is the shortest valid form.
        if (value.Name is null && value.UiName is null && value.PadTo is null)
        {
            writer.WriteStringValue(value.TypeReference);
            return;
        }

        writer.WriteStartObject();
        if (value.Name is not null)
        {
            writer.WriteString("name", value.Name);
        }

        if (value.UiName is not null)
        {
            writer.WriteString("ui_name", value.UiName);
        }

        writer.WriteString("type_reference", value.TypeReference);
        if (value.PadTo is not null)
        {
            writer.WriteNumber("pad_to", value.PadTo.Value);
        }

        writer.WriteEndObject();
    }
}
