// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>Every public type of the core-tier contract has an entry with a purpose paragraph and a usage block in the reference.</summary>
internal sealed class ContractReferenceTests
{
    #region Helpers

    private const string _ReferencePath = "docs/plugins/reference.md";

    /// <summary>The C# name of an exported type: generic types as <c>Name&lt;T&gt;</c> with their parameter names.</summary>
    private static string _CSharpName(Type type)
    {
        if (!type.IsGenericTypeDefinition)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(static argument => argument.Name)) + ">";
    }

    /// <summary>Splits the reference into the lines of each <c>### </c> entry, keyed by the heading text.</summary>
    private static Dictionary<string, List<string>> _Entries()
    {
        Dictionary<string, List<string>> entries = new(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (string line in MarkdownFiles.ReadLines(_ReferencePath))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                current = [];
                entries[line[4..].Trim()] = current;
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal))
            {
                current = null;
            }
            else
            {
                current?.Add(line);
            }
        }

        return entries;
    }

    #endregion

    #region Tests

    [Test]
    public async Task EveryPublicTypeOfTheContractHasAnEntryWithPurposeAndUsage()
    {
        Dictionary<string, List<string>> entries = _Entries();
        string[] types = [.. typeof(IPlugin).Assembly.GetExportedTypes().Select(_CSharpName).Order(StringComparer.Ordinal)];

        List<string> problems = [];
        foreach (string type in types)
        {
            if (!entries.TryGetValue(type, out List<string>? lines))
            {
                problems.Add($"{type}: no '### {type}' entry in {_ReferencePath}");
                continue;
            }

            // Purpose: a non-empty paragraph before the usage block; usage: a "**Usage**" line followed by a C# fence with content.
            int usage = lines.FindIndex(static line => line.Trim() == "**Usage**");
            string? purpose = lines.Take(usage < 0 ? lines.Count : usage).FirstOrDefault(static line => line.Trim().Length > 0);
            if (purpose is null)
            {
                problems.Add($"{type}: the entry has no purpose paragraph");
            }

            int fence = usage < 0 ? -1 : lines.FindIndex(usage, static line => line.TrimStart().StartsWith("```csharp", StringComparison.Ordinal));
            bool hasCode = fence >= 0 && fence + 1 < lines.Count && !lines[fence + 1].TrimStart().StartsWith("```", StringComparison.Ordinal);
            if (!hasCode)
            {
                problems.Add($"{type}: the entry has no usage block");
            }
        }

        await Assert.That(types.Length).IsEqualTo(14);
        await Assert.That(problems).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheReferenceNamesNoTypeThatTheContractDoesNotHave()
    {
        HashSet<string> exported = [.. typeof(IPlugin).Assembly.GetExportedTypes().Select(_CSharpName)];

        string[] unknown = [.. _Entries().Keys.Where(name => !exported.Contains(name)).Order(StringComparer.Ordinal)];

        await Assert.That(unknown).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
    }

    #endregion
}
