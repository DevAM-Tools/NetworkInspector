// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Aggregated metadata extracted from a single <c>[Protocol]</c>-annotated class.
/// <para>
/// Implements <see cref="IEquatable{T}"/> so the Roslyn incremental pipeline can determine whether
/// previously cached output is still valid without re-running the full generator. Equality
/// intentionally excludes <see cref="ClassLocation"/> because it is a runtime-dependent value
/// derived from the same syntax node and is not part of the semantic equality of the extracted data.
/// </para>
/// <para>
/// Collections are stored as <see cref="ImmutableArray{T}"/> rather than <see cref="List{T}"/>
/// so the cached value cannot be mutated after extraction. Mutable backing collections in
/// pipeline DTOs are a known foot-gun: a stale cache hit could expose a list that has since
/// been mutated by a subsequent extraction pass.
/// </para>
/// </summary>
internal sealed class ProtocolInfo : IEquatable<ProtocolInfo>
{
    /// <summary>Initializes a new <see cref="ProtocolInfo"/>. Mutable input lists are frozen
    /// into <see cref="ImmutableArray{T}"/> so callers cannot retroactively change the cached state.</summary>
    public ProtocolInfo(
        string ns, string className, string protocolName, string uiName, string? description,
        IReadOnlyList<FieldInfo> fields, IReadOnlyList<ProtocolTableInfo> protocolTables,
        IReadOnlyList<UsesTableInfo> usesTables,
        IReadOnlyList<SettingInfo> settings, IReadOnlyList<TableRegistration> tableRegistrations,
        IReadOnlyList<string> indexGroups, IReadOnlyList<DiagnosticInfo> diagnostics,
        LocationInfo classLocation)
    {
        Namespace = ns;
        ClassName = className;
        ProtocolName = protocolName;
        UiName = uiName;
        Description = description;
        Fields = fields.ToImmutableArray();
        ProtocolTables = protocolTables.ToImmutableArray();
        UsesTables = usesTables.ToImmutableArray();
        Settings = settings.ToImmutableArray();
        TableRegistrations = tableRegistrations.ToImmutableArray();
        IndexGroups = indexGroups.ToImmutableArray();
        Diagnostics = diagnostics.ToImmutableArray();
        ClassLocation = classLocation;
    }

    /// <summary>CLR namespace of the protocol class.</summary>
    public string Namespace
    {
        get;
    }

    /// <summary>Simple class name (without namespace).</summary>
    public string ClassName
    {
        get;
    }

    /// <summary>Machine-readable protocol name from the <c>[Protocol]</c> attribute.</summary>
    public string ProtocolName
    {
        get;
    }

    /// <summary>Human-readable UI name from the <c>[Protocol]</c> attribute.</summary>
    public string UiName
    {
        get;
    }

    /// <summary>Optional description from the <c>[Protocol]</c> attribute.</summary>
    public string? Description
    {
        get;
    }

    /// <summary>All fields extracted from <c>[Field]</c> attributes.</summary>
    public ImmutableArray<FieldInfo> Fields
    {
        get;
    }

    /// <summary>All protocol dispatch tables extracted from <c>[ProtocolTable]</c> attributes.</summary>
    public ImmutableArray<ProtocolTableInfo> ProtocolTables
    {
        get;
    }

    /// <summary>All external table references extracted from <c>[UsesTable]</c> attributes.</summary>
    public ImmutableArray<UsesTableInfo> UsesTables
    {
        get;
    }

    /// <summary>All setting registrations extracted from setting attributes.</summary>
    public ImmutableArray<SettingInfo> Settings
    {
        get;
    }

    /// <summary>All dispatch-table registrations extracted from <c>[RegisterAtTable]</c> and related attributes.</summary>
    public ImmutableArray<TableRegistration> TableRegistrations
    {
        get;
    }

    /// <summary>All index group names referenced by fields.</summary>
    public ImmutableArray<string> IndexGroups
    {
        get;
    }

    /// <summary>Deferred diagnostics discovered during extraction.</summary>
    public ImmutableArray<DiagnosticInfo> Diagnostics
    {
        get;
    }

    /// <summary>Source location of the class declaration (not compared for incremental equality).</summary>
    public LocationInfo ClassLocation
    {
        get;
    }

    /// <inheritdoc />
    public bool Equals(ProtocolInfo? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        // ClassLocation is intentionally excluded: it is a runtime location value from the same syntax
        // node and does not affect the generated output — including it would cause unnecessary re-runs.
        return Namespace == other.Namespace
            && ClassName == other.ClassName
            && ProtocolName == other.ProtocolName
            && UiName == other.UiName
            && Description == other.Description
            && Fields.SequenceEqual(other.Fields)
            && ProtocolTables.SequenceEqual(other.ProtocolTables)
            && UsesTables.SequenceEqual(other.UsesTables)
            && Settings.SequenceEqual(other.Settings)
            && TableRegistrations.SequenceEqual(other.TableRegistrations)
            && IndexGroups.SequenceEqual(other.IndexGroups)
            && Diagnostics.SequenceEqual(other.Diagnostics);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ProtocolInfo);

    /// <inheritdoc />
    public override int GetHashCode()
        => (Namespace, ClassName, ProtocolName, UiName, Description, Fields.Length, Settings.Length).GetHashCode();
}
