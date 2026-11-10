// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits;

/// <summary>
/// Checks a list of <see cref="LimitDefinition"/> objects as a whole: what no single definition can see about its siblings.
/// Stateless and thread-safe.
/// </summary>
/// <remarks>
/// Call it from one test per project that owns limits, with the list the project publishes (its catalog class), so a copy-paste
/// of a key fails the test and not a customer's machine.
/// </remarks>
/// <seealso cref="LimitCatalogProblem"/>
/// <seealso cref="LimitDefinition"/>
public static class LimitCatalog
{
    #region Check

    /// <summary>Finds duplicate keys.</summary>
    /// <param name="definitions">The definitions of one catalog; may be empty.</param>
    /// <returns>One problem per duplicated key; empty when the catalog is sound.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definitions"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An element of <paramref name="definitions"/> is <see langword="null"/>; the message names its index.</exception>
    /// <remarks>
    /// Cost: one pass over the list, linear in its size; a test-time tool, not a hot path. Keys compare case-sensitively, like
    /// the key grammar (<c>maxItems</c> and <c>maxitems</c> are two keys).
    /// </remarks>
    public static IReadOnlyList<LimitCatalogProblem> Check(IReadOnlyList<LimitDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        // One pass: count each key (ordinal) and remember the order in which the second occurrences appear.
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        List<string> duplicateOrder = [];
        for (int index = 0; index < definitions.Count; index++)
        {
            LimitDefinition? definition = definitions[index];
            if (definition is null)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The definition at index {index} is null."), nameof(definitions));
            }

            counts.TryGetValue(definition.Key, out int seen);
            counts[definition.Key] = seen + 1;
            if (seen == 1)
            {
                duplicateOrder.Add(definition.Key);
            }
        }

        List<LimitCatalogProblem> problems = [];
        for (int index = 0; index < duplicateOrder.Count; index++)
        {
            string key = duplicateOrder[index];
            int count = counts[key];
            string times = count == 2
                ? "twice"
                : string.Create(CultureInfo.InvariantCulture, $"{count} times");
            string message = string.Create(CultureInfo.InvariantCulture, $"The key '{key}' appears {times}; a key names one limit.");
            problems.Add(new LimitCatalogProblem(key, message));
        }

        return problems;
    }

    #endregion
}
