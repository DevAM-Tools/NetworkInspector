// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Metadata for a single setting extracted from a <c>[*Setting]</c>-annotated member.
/// </summary>
internal readonly record struct SettingInfo(
    string FieldName,
    string Name,
    string UiName,
    string GroupName,
    string SettingType,
    string DefaultValue,
    string? Description,
    string? Min = null,
    string? Max = null,
    string? EnumValues = null);
