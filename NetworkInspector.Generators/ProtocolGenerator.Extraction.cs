// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using NetworkInspector.Generators.Models;

namespace NetworkInspector.Generators;

/// <summary>
/// Symbol traversal and metadata extraction for <see cref="ProtocolGenerator"/>.
/// All public attribute payloads are extracted from <see cref="INamedTypeSymbol"/> /
/// <see cref="IFieldSymbol"/> in this partial.
/// </summary>
public sealed partial class ProtocolGenerator
{
    #region Extraction

    /// <summary>
    /// Extracts all protocol metadata from a <c>[Protocol]</c>-annotated class symbol.
    /// Returns <see langword="null"/> only when the <c>[Protocol]</c> attribute itself is
    /// missing or its mandatory constructor arguments are absent; all other errors are
    /// captured as <see cref="DiagnosticInfo"/> entries inside the returned <see cref="ProtocolInfo"/>.
    /// </summary>
    private static ProtocolInfo? ExtractProtocolInfo(INamedTypeSymbol classSymbol, LocationInfo classLocation)
    {
        List<DiagnosticInfo> diagnostics = [];

        // Structural validation — emit diagnostics then bail; generated code would be invalid.
        if (classSymbol.TypeParameters.Length > 0)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagGenericProtocol, classSymbol.Name));
        }

        if (classSymbol.ContainingType is not null)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagNestedProtocol, classSymbol.Name));
        }

        if (classSymbol.ContainingNamespace.IsGlobalNamespace)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagGlobalNamespace, classSymbol.Name));
        }

        // NIGEN012: every declaration must be 'partial' so the generated companion file can
        // contribute additional members without producing CS0260 from the generated source.
        if (!IsDeclaredPartial(classSymbol))
        {
            diagnostics.Add(new DiagnosticInfo(_DiagNotPartial, classSymbol.Name));
        }

        if (diagnostics.Count > 0)
        {
            string ns = classSymbol.ContainingNamespace.IsGlobalNamespace
                ? "<global>"
                : classSymbol.ContainingNamespace.ToDisplayString();
            return new ProtocolInfo(ns, classSymbol.Name, "", "", null, [], [], [], [], [], [], diagnostics, classLocation);
        }

        // Extract [Protocol] constructor arguments
        string? protocolName = null;
        string? uiName = null;
        string? description = null;

        foreach (AttributeData attr in classSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != _FqnProtocolAttribute)
            {
                continue;
            }

            if (attr.ConstructorArguments.Length >= 2)
            {
                protocolName = attr.ConstructorArguments[0].Value as string;
                uiName = attr.ConstructorArguments[1].Value as string;
            }

            foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
            {
                if (named.Key == "Description")
                {
                    description = named.Value.Value as string;
                }
            }

            break; // Only the first [Protocol] attribute matters.
        }

        if (protocolName is null || uiName is null)
        {
            return null;
        }

        // NIGEN004: validate the protocol name; an invalid name would flow into generated
        // identifiers and break compilation downstream.
        if (!IsValidGroupOrTableName(protocolName))
        {
            diagnostics.Add(new DiagnosticInfo(_DiagInvalidIdentifierName, protocolName));
        }

        string namespaceName = classSymbol.ContainingNamespace.ToDisplayString();
        string className = classSymbol.Name;

        List<TableRegistration> tableRegistrations = ExtractTableRegistrations(classSymbol, className, diagnostics);

        List<FieldInfo> fields = [];
        List<ProtocolTableInfo> protocolTables = [];
        List<UsesTableInfo> usesTables = [];
        List<SettingInfo> settings = [];
        HashSet<string> indexGroupSet = [];
        List<string> indexGroups = [];

        ExtractFieldsTablesSettings(classSymbol, className, fields, protocolTables, usesTables, settings, indexGroupSet, indexGroups, diagnostics);
        ValidateDuplicates(className, fields, settings, protocolTables, diagnostics);

        // Sort index groups lexicographically so the generated output is deterministic regardless
        // of field declaration order in the source file. This matters for reproducible builds and
        // makes diff-reviews stable.
        indexGroups.Sort(StringComparer.Ordinal);

        return new ProtocolInfo(
            namespaceName, className, protocolName, uiName, description,
            fields, protocolTables, usesTables, settings, tableRegistrations, indexGroups, diagnostics, classLocation);
    }

    /// <summary>Extracts class-level [RegisterAt*Table] attributes into <see cref="TableRegistration"/> entries.</summary>
    private static List<TableRegistration> ExtractTableRegistrations(INamedTypeSymbol classSymbol, string className, List<DiagnosticInfo> diagnostics)
    {
        List<TableRegistration> registrations = [];

        foreach (AttributeData attr in classSymbol.GetAttributes())
        {
            string? fqn = attr.AttributeClass?.ToDisplayString();

            if (fqn == _FqnRegisterAtTableAttribute)
            {
                if (attr.ConstructorArguments.Length < 2)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "RegisterAtTable", className, className));
                    continue;
                }
                if (attr.ConstructorArguments[0].Value is not string table)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "RegisterAtTable", className, className));
                    continue;
                }
                ulong key = TryToUInt64(attr.ConstructorArguments[1].Value);
                registrations.Add(new TableRegistration(table, key));
            }
            else if (fqn == _FqnRegisterAtStringTableAttribute)
            {
                if (attr.ConstructorArguments.Length < 2)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "RegisterAtStringTable", className, className));
                    continue;
                }
                if (attr.ConstructorArguments[0].Value is not string table
                    || attr.ConstructorArguments[1].Value is not string key)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "RegisterAtStringTable", className, className));
                    continue;
                }
                registrations.Add(new TableRegistration(table, key));
            }
            else if (fqn == _FqnRegisterAtBoolTableAttribute)
            {
                if (attr.ConstructorArguments.Length < 2)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "RegisterAtBoolTable", className, className));
                    continue;
                }
                string? tableMaybe = attr.ConstructorArguments[0].Value as string;
                object? keyObj = attr.ConstructorArguments[1].Value;
                if (keyObj is not bool boolKey)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagInvalidBoolKey, tableMaybe ?? "", className));
                    continue;
                }

                if (tableMaybe is null)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "RegisterAtBoolTable", className, className));
                    continue;
                }
                registrations.Add(new TableRegistration(tableMaybe, boolKey));
            }
            else if (fqn == _FqnRegisterAtBytesTableAttribute)
            {
                if (attr.ConstructorArguments.Length < 2)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "RegisterAtBytesTable", className, className));
                    continue;
                }
                if (attr.ConstructorArguments[0].Value is not string table
                    || attr.ConstructorArguments[1].Kind != TypedConstantKind.Array)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "RegisterAtBytesTable", className, className));
                    continue;
                }
                ImmutableArray<TypedConstant> values = attr.ConstructorArguments[1].Values;
                byte[] keyBytes = new byte[values.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    keyBytes[i] = values[i].Value is byte b ? b : (byte)TryToUInt64(values[i].Value);
                }

                registrations.Add(TableRegistration.ForBytes(table, BytesToHex(keyBytes)));
            }
            else if (fqn == _FqnRegisterAtAnyTableAttribute)
            {
                if (attr.ConstructorArguments.Length < 1)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "RegisterAtAnyTable", className, className));
                    continue;
                }
                if (attr.ConstructorArguments[0].Value is not string table)
                {
                    diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "RegisterAtAnyTable", className, className));
                    continue;
                }
                registrations.Add(new TableRegistration(table));
            }
        }

        return registrations;
    }

    /// <summary>
    /// Iterates all instance fields of the class and populates fields, protocol tables,
    /// uses-table references, settings, and index groups.
    /// Attribute FQN comparison prevents name-hijacking from attributes in other namespaces.
    /// </summary>
    private static void ExtractFieldsTablesSettings(
        INamedTypeSymbol classSymbol,
        string className,
        List<FieldInfo> fields,
        List<ProtocolTableInfo> protocolTables,
        List<UsesTableInfo> usesTables,
        List<SettingInfo> settings,
        HashSet<string> indexGroupSet,
        List<string> indexGroups,
        List<DiagnosticInfo> diagnostics)
    {
        foreach (ISymbol member in classSymbol.GetMembers())
        {
            if (member is not IFieldSymbol fieldSymbol)
            {
                continue;
            }

            foreach (AttributeData attr in fieldSymbol.GetAttributes())
            {
                string? fqn = attr.AttributeClass?.ToDisplayString();
                if (fqn is null)
                {
                    continue;
                }

                string attrShortName = attr.AttributeClass!.Name;

                // Field attributes: namespace prefix ensures we only process our own.
                if (fqn.StartsWith(_FqnNs, StringComparison.Ordinal)
                    && attrShortName.EndsWith(_FieldAttributeSuffix, StringComparison.Ordinal))
                {
                    FieldInfo? fi = ExtractFieldInfo(fieldSymbol, attr, attrShortName, className, diagnostics);
                    if (fi is not null)
                    {
                        fields.Add(fi);
                        if (fi.IndexGroup is not null && indexGroupSet.Add(fi.IndexGroup))
                        {
                            indexGroups.Add(fi.IndexGroup);
                        }
                    }
                }
                else if (fqn == _FqnProtocolTableU64Attribute)
                {
                    ProtocolTableInfo? ti = ExtractProtocolTableInfo(fieldSymbol, attr, "U64", className, diagnostics);
                    if (ti is not null)
                    {
                        protocolTables.Add(ti);
                    }
                }
                else if (fqn == _FqnProtocolTableStringAttribute)
                {
                    ProtocolTableInfo? ti = ExtractProtocolTableInfo(fieldSymbol, attr, "String", className, diagnostics);
                    if (ti is not null)
                    {
                        protocolTables.Add(ti);
                    }
                }
                else if (fqn == _FqnProtocolTableBytesAttribute)
                {
                    ProtocolTableInfo? ti = ExtractProtocolTableInfo(fieldSymbol, attr, "Bytes", className, diagnostics);
                    if (ti is not null)
                    {
                        protocolTables.Add(ti);
                    }
                }
                else if (fqn == _FqnProtocolTableBoolAttribute)
                {
                    ProtocolTableInfo? ti = ExtractProtocolTableInfo(fieldSymbol, attr, "Bool", className, diagnostics);
                    if (ti is not null)
                    {
                        protocolTables.Add(ti);
                    }
                }
                else if (fqn == _FqnProtocolTableAnyAttribute)
                {
                    ProtocolTableInfo? ti = ExtractProtocolTableInfo(fieldSymbol, attr, "Any", className, diagnostics);
                    if (ti is not null)
                    {
                        protocolTables.Add(ti);
                    }
                }
                else if (fqn == _FqnUsesTableAttribute)
                {
                    if (attr.ConstructorArguments.Length < 1)
                    {
                        diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "UsesTableAttribute", fieldSymbol.Name, className));
                    }
                    else if (attr.ConstructorArguments[0].Value is string tableName)
                    {
                        usesTables.Add(new UsesTableInfo(fieldSymbol.Name, tableName));
                    }
                    else
                    {
                        diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "UsesTableAttribute", fieldSymbol.Name, className));
                    }
                }
                else if (fqn == _FqnBoolSettingAttribute)
                {
                    SettingInfo? si = ExtractBoolSettingInfo(fieldSymbol, attr, className, diagnostics);
                    if (si is not null)
                    {
                        settings.Add(si);
                    }
                }
                else if (fqn == _FqnStringSettingAttribute)
                {
                    SettingInfo? si = ExtractStringSettingInfo(fieldSymbol, attr, className, diagnostics);
                    if (si is not null)
                    {
                        settings.Add(si);
                    }
                }
                else if (fqn == _FqnF64SettingAttribute)
                {
                    SettingInfo? si = ExtractF64SettingInfo(fieldSymbol, attr, className, diagnostics);
                    if (si is not null)
                    {
                        settings.Add(si);
                    }
                }
                else if (fqn == _FqnU64SettingAttribute)
                {
                    SettingInfo? si = ExtractU64SettingInfo(fieldSymbol, attr, className, diagnostics);
                    if (si is not null)
                    {
                        settings.Add(si);
                    }
                }
                else if (fqn == _FqnI64SettingAttribute)
                {
                    SettingInfo? si = ExtractI64SettingInfo(fieldSymbol, attr, className, diagnostics);
                    if (si is not null)
                    {
                        settings.Add(si);
                    }
                }
                else if (fqn == _FqnBytesSettingAttribute)
                {
                    SettingInfo? si = ExtractBytesSettingInfo(fieldSymbol, attr, className, diagnostics);
                    if (si is not null)
                    {
                        settings.Add(si);
                    }
                }
                else if (fqn == _FqnEnumSettingAttribute)
                {
                    SettingInfo? si = ExtractEnumSettingInfo(fieldSymbol, attr, className, diagnostics);
                    if (si is not null)
                    {
                        settings.Add(si);
                    }
                }
            }
        }
    }

    /// <summary>Extracts a single field's metadata from a recognized <c>[*Field]</c> attribute.</summary>
    private static FieldInfo? ExtractFieldInfo(
        IFieldSymbol fieldSymbol, AttributeData attr, string attrShortName, string className, List<DiagnosticInfo> diagnostics)
    {
        if (attr.ConstructorArguments.Length < 2)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, attrShortName, fieldSymbol.Name, className));
            return null;
        }

        if (attr.ConstructorArguments[0].Value is not string name
            || attr.ConstructorArguments[1].Value is not string uiName)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, attrShortName, fieldSymbol.Name, className));
            return null;
        }

        // NIGEN004: field names participate in generated identifier construction.
        if (!IsValidGroupOrTableName(name))
        {
            diagnostics.Add(new DiagnosticInfo(_DiagInvalidIdentifierName, name));
        }

        string? indexGroup = null;
        string? desc = null;
        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            if (named.Key == "IndexGroup")
            {
                string? raw = named.Value.Value as string;
                // Treat empty/whitespace as "not set" to avoid generating an empty identifier.
                indexGroup = string.IsNullOrWhiteSpace(raw) ? null : raw;
            }
            else if (named.Key == "Description")
            {
                desc = named.Value.Value as string;
            }
        }

        // Validate that the index group name only contains safe identifier characters.
        if (indexGroup is not null && !IsValidGroupOrTableName(indexGroup))
        {
            diagnostics.Add(new DiagnosticInfo(_DiagInvalidIdentifierName, indexGroup!));
            indexGroup = null; // Drop rather than generate an invalid C# identifier.
        }

        // Map attribute short name to a fully-qualified FieldType enum member.
        // Only matches attributes that passed the namespace prefix check in the caller.
        bool isKnown = true;
        string fieldType = attrShortName switch
        {
            "NoneFieldAttribute" => $"{_GloFieldType}.None",
            "I64FieldAttribute" => $"{_GloFieldType}.I64",
            "U64FieldAttribute" => $"{_GloFieldType}.U64",
            "F64FieldAttribute" => $"{_GloFieldType}.F64",
            "StringFieldAttribute" => $"{_GloFieldType}.String",
            "BytesFieldAttribute" => $"{_GloFieldType}.Bytes",
            "MacFieldAttribute" => $"{_GloFieldType}.MacAddress",
            "IPv4FieldAttribute" => $"{_GloFieldType}.IPv4Address",
            "IPv6FieldAttribute" => $"{_GloFieldType}.IPv6Address",
            "Eui64FieldAttribute" => $"{_GloFieldType}.Eui64",
            "UuidFieldAttribute" => $"{_GloFieldType}.Uuid",
            "TimestampFieldAttribute" => $"{_GloFieldType}.Timestamp",
            "BoolFieldAttribute" => $"{_GloFieldType}.Bool",
            _ => SetUnknown(out isKnown, $"{_GloFieldType}.None")
        };

        if (!isKnown)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagUnknownFieldAttribute, attrShortName, fieldSymbol.Name, className));
        }

        return new FieldInfo(fieldSymbol.Name, name, uiName, fieldType, indexGroup, desc);
    }

    /// <summary>Extracts a protocol-table descriptor for the given key type.</summary>
    private static ProtocolTableInfo? ExtractProtocolTableInfo(
        IFieldSymbol fieldSymbol, AttributeData attr, string keyType, string className, List<DiagnosticInfo> diagnostics)
    {
        string attrShortName = $"ProtocolTable{keyType}Attribute";
        if (attr.ConstructorArguments.Length < 2)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, attrShortName, fieldSymbol.Name, className));
            return null;
        }

        if (attr.ConstructorArguments[0].Value is not string name
            || attr.ConstructorArguments[1].Value is not string uiName)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, attrShortName, fieldSymbol.Name, className));
            return null;
        }

        string? desc = null;
        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            if (named.Key == "Description")
            {
                desc = named.Value.Value as string;
            }
        }

        return new ProtocolTableInfo(fieldSymbol.Name, name, uiName, keyType, desc);
    }

    /// <summary>Extracts a <c>[BoolSetting]</c> descriptor.</summary>
    private static SettingInfo? ExtractBoolSettingInfo(IFieldSymbol fieldSymbol, AttributeData attr, string className, List<DiagnosticInfo> diagnostics)
    {
        if (attr.ConstructorArguments.Length < 3)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "BoolSettingAttribute", fieldSymbol.Name, className));
            return null;
        }
        if (attr.ConstructorArguments[0].Value is not string name
            || attr.ConstructorArguments[1].Value is not string uiName
            || attr.ConstructorArguments[2].Value is not string groupName)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "BoolSettingAttribute", fieldSymbol.Name, className));
            return null;
        }

        bool defaultValue = false;
        string? desc = null;
        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            if (named.Key == "Default" && named.Value.Value is bool b)
            {
                defaultValue = b;
            }
            else if (named.Key == "Description")
            {
                desc = named.Value.Value as string;
            }
        }

        return new SettingInfo(fieldSymbol.Name, name, uiName, groupName, "Bool",
            defaultValue.ToString().ToLowerInvariant(), desc);
    }

    /// <summary>Extracts a <c>[StringSetting]</c> descriptor.</summary>
    private static SettingInfo? ExtractStringSettingInfo(IFieldSymbol fieldSymbol, AttributeData attr, string className, List<DiagnosticInfo> diagnostics)
    {
        if (attr.ConstructorArguments.Length < 3)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "StringSettingAttribute", fieldSymbol.Name, className));
            return null;
        }
        if (attr.ConstructorArguments[0].Value is not string name
            || attr.ConstructorArguments[1].Value is not string uiName
            || attr.ConstructorArguments[2].Value is not string groupName)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "StringSettingAttribute", fieldSymbol.Name, className));
            return null;
        }

        string defaultValue = "";
        string? desc = null;
        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            if (named.Key == "Default" && named.Value.Value is string s)
            {
                defaultValue = s;
            }
            else if (named.Key == "Description")
            {
                desc = named.Value.Value as string;
            }
        }

        return new SettingInfo(fieldSymbol.Name, name, uiName, groupName, "String", defaultValue, desc);
    }

    /// <summary>Extracts an <c>[F64Setting]</c> descriptor.</summary>
    private static SettingInfo? ExtractF64SettingInfo(IFieldSymbol fieldSymbol, AttributeData attr, string className, List<DiagnosticInfo> diagnostics)
    {
        if (attr.ConstructorArguments.Length < 3)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "F64SettingAttribute", fieldSymbol.Name, className));
            return null;
        }
        if (attr.ConstructorArguments[0].Value is not string name
            || attr.ConstructorArguments[1].Value is not string uiName
            || attr.ConstructorArguments[2].Value is not string groupName)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "F64SettingAttribute", fieldSymbol.Name, className));
            return null;
        }

        double defaultValue = 0.0;
        double min = double.NaN;
        double max = double.NaN;
        string? desc = null;
        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            if (named.Key == "Default" && named.Value.Value is double d)
            {
                defaultValue = d;
            }
            else if (named.Key == "Min" && named.Value.Value is double dMin)
            {
                min = dMin;
            }
            else if (named.Key == "Max" && named.Value.Value is double dMax)
            {
                max = dMax;
            }
            else if (named.Key == "Description")
            {
                desc = named.Value.Value as string;
            }
        }

        string defaultStr = defaultValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string? minStr = double.IsNaN(min) ? null : min.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string? maxStr = double.IsNaN(max) ? null : max.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        return new SettingInfo(fieldSymbol.Name, name, uiName, groupName, "F64", defaultStr, desc, minStr, maxStr);
    }

    /// <summary>Extracts a <c>[U64Setting]</c> descriptor.</summary>
    private static SettingInfo? ExtractU64SettingInfo(IFieldSymbol fieldSymbol, AttributeData attr, string className, List<DiagnosticInfo> diagnostics)
    {
        if (attr.ConstructorArguments.Length < 3)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "U64SettingAttribute", fieldSymbol.Name, className));
            return null;
        }
        if (attr.ConstructorArguments[0].Value is not string name
            || attr.ConstructorArguments[1].Value is not string uiName
            || attr.ConstructorArguments[2].Value is not string groupName)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "U64SettingAttribute", fieldSymbol.Name, className));
            return null;
        }

        ulong defaultValue = 0;
        bool hasMin = false;
        ulong min = 0;
        bool hasMax = false;
        ulong max = 0;
        string? desc = null;
        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            if (named.Key == "Default")
            {
                defaultValue = TryToUInt64(named.Value.Value);
            }
            else if (named.Key == "HasMin" && named.Value.Value is bool bMin)
            {
                hasMin = bMin;
            }
            else if (named.Key == "HasMax" && named.Value.Value is bool bMax)
            {
                hasMax = bMax;
            }
            else if (named.Key == "Min")
            {
                min = TryToUInt64(named.Value.Value);
            }
            else if (named.Key == "Max")
            {
                max = TryToUInt64(named.Value.Value);
            }
            else if (named.Key == "Description")
            {
                desc = named.Value.Value as string;
            }
        }

        string defaultStr = defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? minStr = hasMin ? min.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        string? maxStr = hasMax ? max.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        return new SettingInfo(fieldSymbol.Name, name, uiName, groupName, "U64", defaultStr, desc, minStr, maxStr);
    }

    /// <summary>Extracts an <c>[I64Setting]</c> descriptor.</summary>
    private static SettingInfo? ExtractI64SettingInfo(IFieldSymbol fieldSymbol, AttributeData attr, string className, List<DiagnosticInfo> diagnostics)
    {
        if (attr.ConstructorArguments.Length < 3)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "I64SettingAttribute", fieldSymbol.Name, className));
            return null;
        }
        if (attr.ConstructorArguments[0].Value is not string name
            || attr.ConstructorArguments[1].Value is not string uiName
            || attr.ConstructorArguments[2].Value is not string groupName)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "I64SettingAttribute", fieldSymbol.Name, className));
            return null;
        }

        long defaultValue = 0;
        bool hasMin = false;
        long min = 0;
        bool hasMax = false;
        long max = 0;
        string? desc = null;
        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            if (named.Key == "Default")
            {
                defaultValue = TryToInt64(named.Value.Value);
            }
            else if (named.Key == "HasMin" && named.Value.Value is bool bMin)
            {
                hasMin = bMin;
            }
            else if (named.Key == "HasMax" && named.Value.Value is bool bMax)
            {
                hasMax = bMax;
            }
            else if (named.Key == "Min")
            {
                min = TryToInt64(named.Value.Value);
            }
            else if (named.Key == "Max")
            {
                max = TryToInt64(named.Value.Value);
            }
            else if (named.Key == "Description")
            {
                desc = named.Value.Value as string;
            }
        }

        string defaultStr = defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? minStr = hasMin ? min.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        string? maxStr = hasMax ? max.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        return new SettingInfo(fieldSymbol.Name, name, uiName, groupName, "I64", defaultStr, desc, minStr, maxStr);
    }

    /// <summary>Extracts a <c>[BytesSetting]</c> descriptor and validates <c>DefaultHex</c>.</summary>
    private static SettingInfo? ExtractBytesSettingInfo(IFieldSymbol fieldSymbol, AttributeData attr, string className, List<DiagnosticInfo> diagnostics)
    {
        if (attr.ConstructorArguments.Length < 3)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "BytesSettingAttribute", fieldSymbol.Name, className));
            return null;
        }
        if (attr.ConstructorArguments[0].Value is not string name
            || attr.ConstructorArguments[1].Value is not string uiName
            || attr.ConstructorArguments[2].Value is not string groupName)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "BytesSettingAttribute", fieldSymbol.Name, className));
            return null;
        }

        string defaultHex = "";
        string? desc = null;
        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            if (named.Key == "DefaultHex" && named.Value.Value is string hex)
            {
                defaultHex = hex;
            }
            else if (named.Key == "Description")
            {
                desc = named.Value.Value as string;
            }
        }

        if (!string.IsNullOrEmpty(defaultHex))
        {
            if (defaultHex.Length % 2 != 0 || !IsValidHex(defaultHex))
            {
                diagnostics.Add(new DiagnosticInfo(_DiagInvalidBytesDefaultHex, defaultHex, name, className));
                defaultHex = ""; // Fall back to empty array.
            }
        }

        return new SettingInfo(fieldSymbol.Name, name, uiName, groupName, "Bytes", defaultHex, desc);
    }

    /// <summary>Extracts an <c>[EnumSetting]</c> descriptor; pre-formats the allowed-values tuple list.</summary>
    private static SettingInfo? ExtractEnumSettingInfo(IFieldSymbol fieldSymbol, AttributeData attr, string className, List<DiagnosticInfo> diagnostics)
    {
        if (attr.ConstructorArguments.Length < 3)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "EnumSettingAttribute", fieldSymbol.Name, className));
            return null;
        }
        if (attr.ConstructorArguments[0].Value is not string name
            || attr.ConstructorArguments[1].Value is not string uiName
            || attr.ConstructorArguments[2].Value is not string groupName)
        {
            diagnostics.Add(new DiagnosticInfo(_DiagAttributePayloadIncomplete, "EnumSettingAttribute", fieldSymbol.Name, className));
            return null;
        }

        ulong defaultValue = 0;
        string allowedValues = "";
        string? desc = null;
        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            if (named.Key == "Default")
            {
                defaultValue = TryToUInt64(named.Value.Value);
            }
            else if (named.Key == "AllowedValues" && named.Value.Value is string av)
            {
                allowedValues = av;
            }
            else if (named.Key == "Description")
            {
                desc = named.Value.Value as string;
            }
        }

        // Validate and pre-format enum pairs at extraction time so that emission is trivial
        // and any NIGEN005 diagnostics point back to the attribute (not to generated code).
        string formattedPairs = FormatEnumPairs(allowedValues, name, className, diagnostics);
        string defaultStr = defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new SettingInfo(fieldSymbol.Name, name, uiName, groupName, "Enum", defaultStr, desc, enumValues: formattedPairs);
    }

    #endregion

    #region Validation

    /// <summary>Checks for duplicate field, setting, and table names within one protocol class and appends diagnostics.</summary>
    private static void ValidateDuplicates(
        string className,
        List<FieldInfo> fields,
        List<SettingInfo> settings,
        List<ProtocolTableInfo> tables,
        List<DiagnosticInfo> diagnostics)
    {
        HashSet<string> fieldNames = [];
        foreach (FieldInfo f in fields)
        {
            if (!fieldNames.Add(f.Name))
            {
                diagnostics.Add(new DiagnosticInfo(_DiagDuplicateFieldName, className, f.Name));
            }
        }

        HashSet<string> settingNames = [];
        foreach (SettingInfo s in settings)
        {
            if (!settingNames.Add(s.Name))
            {
                diagnostics.Add(new DiagnosticInfo(_DiagDuplicateSettingName, className, s.Name));
            }
        }

        HashSet<string> tableNames = [];
        foreach (ProtocolTableInfo t in tables)
        {
            if (!tableNames.Add(t.Name))
            {
                diagnostics.Add(new DiagnosticInfo(_DiagDuplicateTableName, className, t.Name));
            }
        }
    }

    #endregion

    #region Extraction Utilities

    /// <summary>
    /// Safely converts a boxed attribute constructor value (which may be <see langword="null"/> or
    /// any integral type) to <see cref="ulong"/>. Returns 0 when the value cannot be converted.
    /// </summary>
    private static ulong TryToUInt64(object? value) => value switch
    {
        null => 0UL,
        byte b => b,
        sbyte s => (ulong)s,
        short s => (ulong)s,
        ushort u => u,
        int i => (ulong)i,
        uint u => u,
        long l => (ulong)l,
        ulong u => u,
        bool bv => bv ? 1UL : 0UL,
        _ => 0UL
    };

    /// <summary>Safely converts a boxed attribute constructor value to <see cref="long"/>.
    /// Returns 0 for null or non-numeric types.</summary>
    private static long TryToInt64(object? value) => value switch
    {
        null => 0L,
        byte b => b,
        sbyte s => s,
        short s => s,
        ushort u => u,
        int i => i,
        uint u => u,
        long l => l,
        ulong u => (long)u,
        bool bv => bv ? 1L : 0L,
        _ => 0L
    };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> consists only of
    /// letters, digits, dots, and underscores ([a-zA-Z0-9._]).
    /// </summary>
    private static bool IsValidGroupOrTableName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when at least one of the user-authored declarations of
    /// <paramref name="classSymbol"/> carries the <c>partial</c> modifier. Required because the
    /// generator emits <c>partial class</c> contributions; a missing modifier would produce
    /// CS0260 from the generated source rather than a meaningful diagnostic on the user's class.
    /// </summary>
    private static bool IsDeclaredPartial(INamedTypeSymbol classSymbol)
    {
        foreach (SyntaxReference syntaxRef in classSymbol.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax classDecl
                && classDecl.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Sets <paramref name="isKnown"/> to <see langword="false"/> and returns
    /// <paramref name="fallback"/>. Used in switch expressions to record an
    /// unknown branch while still returning a valid fallback value.
    /// </summary>
    private static string SetUnknown(out bool isKnown, string fallback)
    {
        isKnown = false;
        return fallback;
    }

    /// <summary>Converts a byte array to an uppercase hex string (e.g. <c>[0x01, 0xAB]</c> -> <c>"01AB"</c>).</summary>
    private static string BytesToHex(byte[] bytes)
    {
        StringBuilder sbHex = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sbHex.AppendFormat("{0:X2}", b);
        }

        return sbHex.ToString();
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="hex"/> is a non-empty, even-length
    /// string whose characters are exclusively hex digits (0-9, a-f, A-F).
    /// </summary>
    private static bool IsValidHex(string hex)
    {
        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            return false;
        }

        foreach (char c in hex)
        {
            // Accept both uppercase and lowercase hex digits to handle user-supplied values.
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses and validates a semicolon-delimited "Name=Value" string, emitting NIGEN005
    /// diagnostics for any entry whose value cannot be parsed as a <see cref="ulong"/>.
    /// Returns the C# tuple initializer string for the generated EnumSettingMetadata.FromPairs call.
    /// </summary>
    private static string FormatEnumPairs(string allowedValues, string settingName, string className, List<DiagnosticInfo> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(allowedValues))
        {
            return "";
        }

        StringBuilder sb = new();
        string[] pairs = allowedValues.Split(';');
        foreach (string rawPair in pairs)
        {
            string pair = rawPair.Trim();
            if (pair.Length == 0)
            {
                continue;
            }

            int eqIndex = pair.IndexOf('=');
            if (eqIndex < 0)
            {
                continue;
            }

            string entryName = pair.Substring(0, eqIndex).Trim();
            string entryValue = pair.Substring(eqIndex + 1).Trim();

            if (!ulong.TryParse(entryValue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                diagnostics.Add(new DiagnosticInfo(_DiagInvalidEnumPairValue, entryName, entryValue, settingName + " in " + className));
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append($"(\"{EscapeString(entryName)}\", {entryValue})");
        }

        return sb.ToString();
    }

    #endregion
}
