// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Holds a pending diagnostic descriptor and its message arguments.
/// Diagnostics are deferred until source emission so that the location of the
/// class declaration is available — the location is not known during extraction.
/// </summary>
internal sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    private readonly DiagnosticDescriptor _Descriptor;
    private readonly string[] _MessageArgs;

    /// <summary>Initializes a new <see cref="DiagnosticInfo"/> with the given descriptor and message arguments.</summary>
    /// <param name="descriptor">The Roslyn diagnostic descriptor (id, severity, message template).</param>
    /// <param name="messageArgs">Format arguments substituted into the descriptor's message template.</param>
    public DiagnosticInfo(DiagnosticDescriptor descriptor, params string[] messageArgs)
    {
        _Descriptor = descriptor;
        _MessageArgs = messageArgs;
    }

    /// <summary>The diagnostic descriptor used for severity and message template.</summary>
    public DiagnosticDescriptor Descriptor => _Descriptor;

    /// <summary>Creates a <see cref="Diagnostic"/> at the specified source location.</summary>
    /// <param name="location">Location to attach the diagnostic to (typically the protocol class declaration).</param>
    public Diagnostic ToDiagnostic(Location location) => Diagnostic.Create(_Descriptor, location, _MessageArgs);

    /// <inheritdoc />
    public bool Equals(DiagnosticInfo? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return _Descriptor.Id == other._Descriptor.Id && _MessageArgs.SequenceEqual(other._MessageArgs);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

    /// <inheritdoc />
    public override int GetHashCode() => (_Descriptor.Id, _MessageArgs.Length).GetHashCode();
}
