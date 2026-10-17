// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter;

#region Kind

/// <summary>What a <see cref="FilterCompletionItem"/> inserts.</summary>
public enum FilterCompletionKind : byte
{
    /// <summary>A protocol name, usable as a presence test (<c>tcp</c>) or scope anchor (<c>$udp</c>).</summary>
    Protocol = 0,

    /// <summary>A canonical field name such as <c>udp.srcport</c>.</summary>
    Field = 1,

    /// <summary>A field alias group such as <c>eth.addr</c> (any member matches).</summary>
    FieldAlias = 2,

    /// <summary>A word of the language: <c>and</c>, <c>or</c>, <c>not</c>, <c>in</c>, <c>contains</c>, <c>flank</c>, …</summary>
    Keyword = 3,

    /// <summary>A symbolic operator: <c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&amp;&amp;</c>, <c>||</c>, …</summary>
    Operator = 4,

    /// <summary>A named <c>flank(…)</c> argument such as <c>within:</c> or <c>changed</c>.</summary>
    FlankArgument = 5,
}

#endregion

#region Item

/// <summary>One completion candidate.</summary>
/// <param name="Text">The text that replaces <see cref="FilterCompletions.ReplaceStart"/> … <see cref="FilterCompletions.ReplaceLength"/>.</param>
/// <param name="Kind">What the text is.</param>
/// <param name="ValueType">
/// The value type of a <see cref="FilterCompletionKind.Field"/>, or of a <see cref="FilterCompletionKind.FieldAlias"/>
/// whose members all share one type; <see cref="FieldType.None"/> otherwise.
/// </param>
public readonly record struct FilterCompletionItem(string Text, FilterCompletionKind Kind, FieldType ValueType);

#endregion

#region Result

/// <summary>
/// The completion candidates for one caret position, produced by <see cref="Filter.GetCompletions"/>.
/// <para>
/// Accepting an item replaces the characters <c>[ReplaceStart, ReplaceStart + ReplaceLength)</c> of the expression
/// with <see cref="FilterCompletionItem.Text"/>. That range is the whole word (or operator) the caret touches, so
/// accepting in the middle of a word replaces the word instead of splitting it.
/// </para>
/// <para><b>Thread-safety:</b> immutable; safe to share across threads.</para>
/// </summary>
public sealed class FilterCompletions
{
    #region Construction

    /// <summary>Creates a result; <paramref name="items"/> is owned by the new instance.</summary>
    internal FilterCompletions(int replaceStart, int replaceLength, string prefix, FilterCompletionItem[] items)
    {
        ReplaceStart = replaceStart;
        ReplaceLength = replaceLength;
        Prefix = prefix;
        Items = Array.AsReadOnly(items);
    }

    #endregion

    #region Properties

    /// <summary>Start offset of the text an accepted item replaces.</summary>
    public int ReplaceStart { get; }

    /// <summary>Length of the text an accepted item replaces; 0 when the caret is between words.</summary>
    public int ReplaceLength { get; }

    /// <summary>The part of the word typed before the caret; every item starts with it (ignoring case).</summary>
    public string Prefix { get; }

    /// <summary>
    /// The candidates in alphabetical order (ordinal, ignoring case). Empty where no completion applies — inside a
    /// string literal, or where a literal value is expected (after <c>==</c>, inside <c>in { … }</c>, after
    /// <c>within:</c>).
    /// </summary>
    public IReadOnlyList<FilterCompletionItem> Items { get; }

    #endregion
}

#endregion
