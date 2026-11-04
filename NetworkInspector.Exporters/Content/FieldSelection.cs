// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Fields to export, resolved by name against one stack. Immutable and thread-safe after creation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why names:</b> field ids are dense counters of one stack (a session restart builds another stack with other ids), so
/// users, CLI options and session requests name fields, and the selection resolves the names once. The exporter then works
/// with ids only: one array lookup per value, no string comparison on the hot path.
/// </para>
/// <para>
/// <b>Trust boundary:</b> names come from users (CLI options, dialogs, MCP requests). Every name is resolved against the stack;
/// an unknown, value-less, duplicate or empty request fails the whole selection with an error that names the field, so a typo
/// can never turn into a silent full export or an empty column. The number of fields is bounded by the stack's field count
/// (a fact of the trusted stack), so no separate limit is needed. Rejected alternative: ignoring unknown names with a warning —
/// a misspelled field would then produce an export that silently lacks the column the user asked for.
/// </para>
/// <para>
/// <b>Watch:</b> <see cref="Watch"/> is built with <see cref="FieldWatch.ForExtraction"/>, which also accepts the other fields of
/// the owning protocols. That expands every deferred container that can hold a selected field during a tree-free parse; a plain
/// <see cref="FieldWatch.Only(IStack, ReadOnlySpan{FieldId})"/> would miss a field inside a lazy container outside its index
/// group. The extra fields reach the collector and are dropped there by one array lookup.
/// </para>
/// </remarks>
public sealed class FieldSelection
{
    #region Configuration and state

    /// <summary>The resolved fields in request order.</summary>
    private readonly SelectedField[] _Fields;

    /// <summary>Creates the selection from fields that <see cref="TryCreate"/> already validated.</summary>
    /// <param name="stack">The stack the names were resolved against.</param>
    /// <param name="fields">The resolved fields in request order.</param>
    /// <param name="watch">The extraction watch of the selected ids.</param>
    /// <param name="displayWatch">The display-text watch of exactly the selected ids.</param>
    private FieldSelection(IStack stack, SelectedField[] fields, FieldWatch watch, FieldWatch displayWatch)
    {
        Stack = stack;
        _Fields = fields;
        Watch = watch;
        DisplayWatch = displayWatch;
    }

    /// <summary>The stack the names were resolved against; packets of another stack are refused by the exporters.</summary>
    public IStack Stack { get; }

    /// <summary>The resolved fields in request order.</summary>
    public IReadOnlyList<SelectedField> Fields => _Fields;

    /// <summary>The watch a tree-free parse needs to deliver every selected field (see the remarks).</summary>
    public FieldWatch Watch { get; }

    /// <summary>
    /// The fields whose custom text and representation the dissectors build: exactly the selected ids. Not the extraction
    /// watch: that one is widened to whole protocols so deferred containers expand, but texts are needed only for the fields
    /// the export writes, and formatting them for every field of a protocol would undo the saving of a selected-fields export.
    /// </summary>
    public FieldWatch DisplayWatch { get; }

    #endregion

    #region Resolving requests

    /// <summary>Resolves <paramref name="requests"/> against <paramref name="stack"/>.</summary>
    /// <param name="stack">Stack whose field registry names the fields.</param>
    /// <param name="requests">Requested fields; at least one.</param>
    /// <param name="defaultOccurrences">Occurrence choice for requests without their own.</param>
    /// <param name="selection">The selection when the method returns <see langword="true"/>.</param>
    /// <param name="error">The first problem found when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when every request resolved to a distinct value field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaultOccurrences"/> or the occurrence choice of a request is not a defined value.
    /// </exception>
    public static bool TryCreate(
        IStack stack,
        ReadOnlySpan<FieldRequest> requests,
        Occurrences defaultOccurrences,
        [NotNullWhen(true)] out FieldSelection? selection,
        out FieldSelectionError error)
    {
        ArgumentNullException.ThrowIfNull(stack);
        _ThrowIfUndefined(defaultOccurrences, nameof(defaultOccurrences));
        selection = null;

        // An empty selection would export packet ids only; refuse it so a missing option is visible.
        if (requests.Length == 0)
        {
            error = new FieldSelectionError(FieldSelectionErrorCode.Empty, string.Empty, "At least one field must be selected.");
            return false;
        }

        SelectedField[] fields = new SelectedField[requests.Length];
        FieldId[] ids = new FieldId[requests.Length];
        for (int i = 0; i < requests.Length; i++)
        {
            FieldRequest request = requests[i];
            string name = request.Name ?? string.Empty;

            // Resolve: whitespace and unknown names are one error class for the user ("no such field").
            FieldId? resolved = string.IsNullOrWhiteSpace(name)
                ? null
                : stack.GetFieldId(name);
            if (resolved is not { } id)
            {
                error = new FieldSelectionError(FieldSelectionErrorCode.UnknownField, name, Invariant($"Unknown field '{name}'."));
                return false;
            }

            // A container has no value column; selecting it would produce an always-empty column.
            FieldInfo info = stack.GetField(id)!; // Invariant: GetFieldId returned an id of this stack.
            if (info.FieldType == FieldType.None)
            {
                error = new FieldSelectionError(
                    FieldSelectionErrorCode.NoValue,
                    name,
                    Invariant($"Field '{name}' is a container without a value; select one of its value fields."));
                return false;
            }

            // Duplicates: linear scan over the already accepted ids (selections are small; no set allocation).
            if (Array.IndexOf(ids, id, 0, i) >= 0)
            {
                error = new FieldSelectionError(FieldSelectionErrorCode.Duplicate, name, Invariant($"Field '{name}' is selected twice."));
                return false;
            }

            Occurrences occurrences = request.Occurrences ?? defaultOccurrences;
            _ThrowIfUndefined(occurrences, nameof(requests));
            ids[i] = id;
            fields[i] = new SelectedField(id, info.Name, info.FieldType, occurrences);
        }

        selection = new FieldSelection(stack, fields, FieldWatch.ForExtraction(stack, ids), FieldWatch.Only(stack, ids));
        error = default;
        return true;
    }

    /// <summary>Throws when <paramref name="occurrences"/> is not a defined <see cref="Occurrences"/> value.</summary>
    /// <param name="occurrences">The choice to check.</param>
    /// <param name="parameterName">The public parameter the choice came from, for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="occurrences"/> is not defined.</exception>
    private static void _ThrowIfUndefined(Occurrences occurrences, string parameterName)
    {
        if (!Enum.IsDefined(occurrences))
        {
            throw new ArgumentOutOfRangeException(parameterName, occurrences, "Unknown occurrence choice.");
        }
    }

    #endregion
}
