// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Tshark;

/// <summary>
/// Single PDML field extracted from tshark output. Carries the field name, raw value,
/// human-readable display text, byte offset and field size.
/// </summary>
/// <param name="Name">tshark field name (for example <c>ip.src</c>).</param>
/// <param name="Value">Raw hex value reported by tshark.</param>
/// <param name="Show">Display text reported by tshark.</param>
/// <param name="ShowName">Full display string including label and value.</param>
/// <param name="Position">Byte offset inside the frame.</param>
/// <param name="Size">Size in bytes.</param>
public readonly record struct PdmlField(
    string Name,
    string? Value,
    string? Show,
    string? ShowName,
    int Position,
    int Size);
