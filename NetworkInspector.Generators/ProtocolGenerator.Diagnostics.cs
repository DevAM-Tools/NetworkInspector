// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators;

/// <summary>
/// Diagnostic descriptors emitted by <see cref="ProtocolGenerator"/>. NIGEN001..NIGEN014.
/// </summary>
public sealed partial class ProtocolGenerator
{
    #region Diagnostics

    /// <summary>NIGEN001: Two or more fields with the same name in one protocol class.</summary>
    private static readonly DiagnosticDescriptor _DiagDuplicateFieldName = new(
        id: "NIGEN001",
        title: "Duplicate field name",
        messageFormat: "Protocol '{0}' declares two or more fields with name '{1}'. Field names must be unique within a protocol.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN002: Two or more settings with the same name in one protocol class.</summary>
    private static readonly DiagnosticDescriptor _DiagDuplicateSettingName = new(
        id: "NIGEN002",
        title: "Duplicate setting name",
        messageFormat: "Protocol '{0}' declares two or more settings with name '{1}'. Setting names must be unique within a protocol.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN003: Two or more dispatch tables with the same name in one protocol class.</summary>
    private static readonly DiagnosticDescriptor _DiagDuplicateTableName = new(
        id: "NIGEN003",
        title: "Duplicate dispatch table name",
        messageFormat: "Protocol '{0}' declares two or more dispatch tables with name '{1}'. Table names must be unique within a protocol.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN004: Index group or table name contains a character outside [a-zA-Z0-9._].</summary>
    private static readonly DiagnosticDescriptor _DiagInvalidIdentifierName = new(
        id: "NIGEN004",
        title: "Invalid character in group or table name",
        messageFormat: "The name '{0}' contains an invalid character. Only letters, digits, dots, and underscores ([a-zA-Z0-9._]) are allowed.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN005: AllowedValues entry has a non-numeric value that cannot be parsed as ulong.</summary>
    private static readonly DiagnosticDescriptor _DiagInvalidEnumPairValue = new(
        id: "NIGEN005",
        title: "Invalid enum pair value",
        messageFormat: "The AllowedValues entry '{0}={1}' for setting '{2}' has a non-numeric value. Values must be non-negative integers parseable as ulong.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN006: [Protocol] class has type parameters.</summary>
    private static readonly DiagnosticDescriptor _DiagGenericProtocol = new(
        id: "NIGEN006",
        title: "Generic protocol class",
        messageFormat: "Protocol class '{0}' is generic. [Protocol] classes must not have type parameters.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN007: [Protocol] class is nested inside another type.</summary>
    private static readonly DiagnosticDescriptor _DiagNestedProtocol = new(
        id: "NIGEN007",
        title: "Nested protocol class",
        messageFormat: "Protocol class '{0}' is nested inside another type. [Protocol] classes must be top-level.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN008: [Protocol] class is in the global namespace.</summary>
    private static readonly DiagnosticDescriptor _DiagGlobalNamespace = new(
        id: "NIGEN008",
        title: "Protocol class in global namespace",
        messageFormat: "Protocol class '{0}' is in the global namespace. [Protocol] classes must be in a named namespace.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN009: Bool table key expression did not evaluate to a boolean value.</summary>
    private static readonly DiagnosticDescriptor _DiagInvalidBoolKey = new(
        id: "NIGEN009",
        title: "Invalid boolean dispatch key",
        messageFormat: "The key in [RegisterAtBoolTable(\"{0}\", ...)] on protocol '{1}' is not a boolean value. Provide either 'true' or 'false'.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN010: Attribute ends with FieldAttribute but does not map to a known FieldType.</summary>
    private static readonly DiagnosticDescriptor _DiagUnknownFieldAttribute = new(
        id: "NIGEN010",
        title: "Unknown field attribute type",
        messageFormat: "The attribute '{0}' on field '{1}' in protocol '{2}' ends with 'FieldAttribute' but maps to no known FieldType."
            + " The field will be registered with FieldType.None.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <summary>NIGEN011: BytesSetting.DefaultHex is not a valid even-length hex string.</summary>
    private static readonly DiagnosticDescriptor _DiagInvalidBytesDefaultHex = new(
        id: "NIGEN011",
        title: "Invalid DefaultHex in bytes setting",
        messageFormat: "The DefaultHex value '{0}' on bytes setting '{1}' in protocol '{2}' is not a valid uppercase hexadecimal string."
            + " DefaultHex must be an even number of hex digits (e.g. \"0102AABB\").",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN012: [Protocol] class is not declared <c>partial</c>; the generated companion file would not compile.</summary>
    private static readonly DiagnosticDescriptor _DiagNotPartial = new(
        id: "NIGEN012",
        title: "Protocol class must be partial",
        messageFormat: "Protocol class '{0}' is not declared 'partial'. The source generator emits a partial class to add registration members;"
            + " the user-authored declaration must therefore use the 'partial' modifier.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>NIGEN013: An attribute payload is missing required positional arguments and the generator skipped it silently.</summary>
    private static readonly DiagnosticDescriptor _DiagAttributePayloadIncomplete = new(
        id: "NIGEN013",
        title: "Attribute payload incomplete",
        messageFormat: "The '{0}' attribute on '{1}' in protocol '{2}' is missing required positional arguments and was skipped."
            + " Provide all positional constructor arguments (name, UI name, [group, ...]).",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <summary>NIGEN014: Array setting Default contains a null element.</summary>
    private static readonly DiagnosticDescriptor _DiagNullArraySettingDefaultElement = new(
        id: "NIGEN014",
        title: "Null element in array setting Default",
        messageFormat: "The Default array of '{0}' on field '{1}' in protocol '{2}' contains a null element. Array setting defaults cannot contain null.",
        category: _DiagCategory, defaultSeverity: DiagnosticSeverity.Error, isEnabledByDefault: true);

    #endregion
}
