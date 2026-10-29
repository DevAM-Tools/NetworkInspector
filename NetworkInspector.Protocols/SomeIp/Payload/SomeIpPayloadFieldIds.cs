// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Payload;

/// <summary>
/// The ids of the fixed payload fields: the nodes the decoder appends that belong to no configured usage site (diagnostics,
/// metadata, the raw remainder) and the typed fallback fields a site decodes into when its own field could not be registered.
/// <para>
/// The protocol declares these fields with attributes, so the generator registers them and assigns the ids; the decoder lives
/// outside the protocol class and cannot read the protocol's private fields, so the protocol copies the ids into one instance
/// after registration and hands it to every decode. One reference instead of a dozen parameters.
/// </para>
/// <para><b>Thread-safety:</b> immutable after construction; safe to read from any thread.</para>
/// </summary>
internal sealed class SomeIpPayloadFieldIds
{
    /// <summary>The bytes behind the last decoded parameter.</summary>
    internal required FieldId Unparsed { get; init; }

    /// <summary>The alignment bytes behind a value (metadata view).</summary>
    internal required FieldId Padding { get; init; }

    /// <summary>The type description of a node (metadata view).</summary>
    internal required FieldId Type { get; init; }

    /// <summary>The value of a length field that the decoder read (metadata view).</summary>
    internal required FieldId Length { get; init; }

    /// <summary>The element count of an array (metadata view).</summary>
    internal required FieldId ArrayCount { get; init; }

    /// <summary>A diagnostic about the payload.</summary>
    internal required FieldId Error { get; init; }

    /// <summary>The fallback field of an unsigned integer site.</summary>
    internal required FieldId ValueU64 { get; init; }

    /// <summary>The fallback field of a signed integer site.</summary>
    internal required FieldId ValueI64 { get; init; }

    /// <summary>The fallback field of a floating-point site.</summary>
    internal required FieldId ValueF64 { get; init; }

    /// <summary>The fallback field of a boolean site.</summary>
    internal required FieldId ValueBool { get; init; }

    /// <summary>The fallback field of a string site.</summary>
    internal required FieldId ValueString { get; init; }

    /// <summary>The fallback field of a UUID site.</summary>
    internal required FieldId ValueUuid { get; init; }

    /// <summary>The fallback field of a container site, a site of an unavailable type, and a dangling reference.</summary>
    internal required FieldId ValueNone { get; init; }

    /// <summary>The tag of a TLV member (metadata view).</summary>
    internal required FieldId TlvTag { get; init; }

    /// <summary>The wire type of a tag, a child of the tag node.</summary>
    internal required FieldId TlvWireType { get; init; }

    /// <summary>The data id of a tag, a child of the tag node.</summary>
    internal required FieldId TlvDataId { get; init; }

    /// <summary>The reserved bit of a tag, a child of the tag node.</summary>
    internal required FieldId TlvReserved { get; init; }

    /// <summary>The value bytes of a TLV member or a union alternative that the description does not define.</summary>
    internal required FieldId TlvUnknown { get; init; }

    /// <summary>The type selector of a union (metadata view).</summary>
    internal required FieldId UnionType { get; init; }
}
