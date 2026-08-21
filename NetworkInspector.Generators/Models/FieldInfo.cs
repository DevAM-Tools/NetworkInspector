// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Metadata for a single field extracted from a <c>[*Field]</c>-annotated member.
/// </summary>
/// <param name="FieldName">C# field name for the generated field ID backing store (e.g., <c>_EtherTypeFieldId</c>).</param>
/// <param name="Name">Machine-readable field name (e.g., <c>"eth.type"</c>).</param>
/// <param name="UiName">Human-readable UI label.</param>
/// <param name="FieldType">Fully-qualified <c>FieldType</c> enum value (e.g., <c>global::NetworkInspector.Core.Fields.FieldType.U64</c>).</param>
/// <param name="IndexGroup">Optional index group name; <see langword="null"/> if the field is not indexed.</param>
/// <param name="Description">Optional description shown in UI/tooling.</param>
internal readonly record struct FieldInfo(
    string FieldName,
    string Name,
    string UiName,
    string FieldType,
    string? IndexGroup,
    string? Description);
