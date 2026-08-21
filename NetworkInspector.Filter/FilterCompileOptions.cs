// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter;

#region Callback surface

/// <summary>Classifies a name span reported by <see cref="FilterFieldNameSpanCallback"/>.</summary>
public enum FilterFieldNameKind : byte
{
    /// <summary>A dotted field path such as <c>udp.port</c>.</summary>
    FieldPath = 0,

    /// <summary>A bare name used as a protocol/presence test such as <c>tcp</c>.</summary>
    ProtocolName = 1,

    /// <summary>A scope anchor name following <c>$</c>.</summary>
    ScopeAnchor = 2,

    /// <summary>
    /// A trailing name the parser could not complete, for example the <c>tcp.por</c> prefix in
    /// <c>"tcp.por == "</c>. Reported so a UI can offer completions mid-edit.
    /// </summary>
    Incomplete = 3,
}

/// <summary>
/// Receives every field, protocol and scope-anchor name span found while lexing and parsing.
/// <para>
/// The callback fires even when compilation ultimately fails, so an editor can offer
/// completions for partially typed expressions. Implementations must not throw; an exception
/// is converted into <see cref="FilterErrorKind.CallbackFailed"/> and aborts the compile.
/// </para>
/// </summary>
/// <param name="expression">The full expression being parsed.</param>
/// <param name="startInclusive">Start offset of the name.</param>
/// <param name="length">Length of the name.</param>
/// <param name="kind">How the parser interpreted the name.</param>
public delegate void FilterFieldNameSpanCallback(
    ReadOnlySpan<char> expression,
    int startInclusive,
    int length,
    FilterFieldNameKind kind);

#endregion

#region Options

/// <summary>
/// Optional inputs for <see cref="Filter.Compile(string, IStack, FilterCompileOptions?)"/> and
/// <see cref="Filter.TryParse(string, FilterCompileOptions?, out FilterError?)"/>.
/// </summary>
public sealed class FilterCompileOptions
{
    #region Properties

    /// <summary>
    /// Optional caret offset (<c>0..expression.Length</c>) for UI completers.
    /// Compilation ignores the value; it is carried through so a completer can decide which
    /// reported span contains the caret.
    /// </summary>
    public int? CaretPosition
    {
        get; init;
    }

    /// <summary>Optional name-span sink used by editors; see <see cref="FilterFieldNameSpanCallback"/>.</summary>
    public FilterFieldNameSpanCallback? OnFieldNameSpan
    {
        get; init;
    }

    /// <summary>
    /// Optional regular-expression timeout for the <c>matches</c> operator.
    /// Defaults to one second, bounding the cost of adversarial patterns on untrusted input.
    /// </summary>
    public TimeSpan? RegexTimeout
    {
        get; init;
    }

    /// <summary>
    /// Optional code-generation backend. Leaving this unset uses
    /// <see cref="ExpressionTreeCodegen"/>. The property is internal because
    /// <see cref="IFilterCodegen"/> is an implementation seam rather than public API.
    /// </summary>
    internal IFilterCodegen? Codegen
    {
        get; init;
    }

    #endregion
}

#endregion
