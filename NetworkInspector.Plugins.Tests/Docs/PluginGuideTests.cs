// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The plugin guide and the protocol chapter cover the tier 1 topics, and the imported design documents are in place.</summary>
internal sealed class PluginGuideTests
{
    #region Helpers

    private static readonly string[] _GuideHeadings =
    [
        "Project setup", "Entry point", "Contributions", "Options and settings", "Lifecycle", "Build", "Publish", "Distribute",
        "Register the plugin", "Pitfalls",
    ];

    private static readonly string[] _MentionedNames =
    [
        "IPlugin", "IPluginBuilder", "IPluginContext", "SourceRegistration", "ExporterRegistration", "IFrameSource",
        "IPacketListener", "SettingsRegistrar", "SettingScope", "plugins.paths", "plugins.startTimeoutSeconds", "ni plugins list",
    ];

    private static readonly string[] _ProtocolHeadings = ["Protocol", "Fields", "Port registration", "Heuristic parser", "Post-parser"];

    private static readonly string[] _RemovedNames =
        ["NiPlugin", "INiPlugin", "ISourceFactory", "IExporterFactory", "ExportSink", "OptionSet", "IPluginRuntime"];

    private static readonly string[] _ImportedDocuments =
    [
        "docs/abgleich_ni_implementation.md", "docs/guide_avalonia_umsetzung.md", "docs/skip-field-tree.md", "docs/value-cache-design.md",
    ];

    private const string _ImportNote = "> **Imported design document.**";

    /// <summary>The relative links of a document that do not point to an existing file or folder.</summary>
    private static List<string> _BrokenLinks(string relativePath)
    {
        string directory = Path.GetDirectoryName(RepositoryRoot.Combine(relativePath))
            ?? throw new InvalidOperationException(relativePath);
        List<string> broken = [];
        foreach (string target in MarkdownFiles.RelativeLinks(MarkdownFiles.ReadLines(relativePath)))
        {
            string full = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(target).Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                broken.Add($"{relativePath}: {target}");
            }
        }

        return broken;
    }

    #endregion

    #region Guide

    [Test]
    public async Task TheGuideCoversTheTierOneTopicsWithoutRemovedNames()
    {
        string[] lines = MarkdownFiles.ReadLines("docs/plugins/guide.md");
        List<string> headings = MarkdownFiles.Headings(lines);
        string text = string.Join('\n', lines);

        string[] missingHeadings = [.. _GuideHeadings.Where(heading => !headings.Contains(heading, StringComparer.Ordinal))];
        string[] missingNames = [.. _MentionedNames.Where(name => !text.Contains(name, StringComparison.Ordinal))];
        string[] removed = [.. _RemovedNames.Where(name => text.Contains(name, StringComparison.Ordinal))];

        await Assert.That(missingHeadings).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
        await Assert.That(missingNames).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
        await Assert.That(removed).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheProtocolChapterHasItsSectionsAndNoRemovedNames()
    {
        string[] lines = MarkdownFiles.ReadLines("docs/plugins/protocols.md");
        List<string> headings = MarkdownFiles.Headings(lines);
        string text = string.Join('\n', lines);

        string[] missing = [.. _ProtocolHeadings
            .Where(heading => !headings.Contains(heading, StringComparer.Ordinal))];
        string[] removed = [.. _RemovedNames.Where(name => text.Contains(name, StringComparison.Ordinal))];

        await Assert.That(missing).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
        await Assert.That(removed).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task EveryRelativeLinkOfThePluginDocumentsAndTheImportedDocumentsResolves()
    {
        string[] pluginDocuments = [.. Directory.EnumerateFiles(RepositoryRoot.Combine("docs/plugins"), "*.md")
            .Select(static path => "docs/plugins/" + Path.GetFileName(path))];

        List<string> broken = [];
        foreach (string document in pluginDocuments.Concat(_ImportedDocuments))
        {
            broken.AddRange(_BrokenLinks(document));
        }

        await Assert.That(pluginDocuments.Length).IsGreaterThanOrEqualTo(3);
        await Assert.That(broken).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
    }

    #endregion

    #region Imported design documents

    [Test]
    public async Task TheFourDesignDocumentsAreImportedInEnglishWithAnImportNote()
    {
        List<string> problems = [];
        foreach (string document in _ImportedDocuments)
        {
            string[] lines = MarkdownFiles.ReadLines(document);
            if (!lines[0].StartsWith("<!-- Copyright ©", StringComparison.Ordinal))
            {
                problems.Add($"{document}: the first line is not the copyright comment");
            }

            string? next = lines.Skip(1).FirstOrDefault(static line => line.Trim().Length > 0);
            if (next is null || !next.StartsWith(_ImportNote, StringComparison.Ordinal))
            {
                problems.Add($"{document}: the import note does not follow the copyright comment");
            }

            foreach (string heading in MarkdownFiles.Headings(lines))
            {
                if (heading.Contains("Anforderungen", StringComparison.Ordinal) || heading.Contains("Umsetzung", StringComparison.Ordinal)
                    || heading.Contains("Abgleich:", StringComparison.Ordinal))
                {
                    problems.Add($"{document}: German heading '{heading}'");
                }
            }
        }

        await Assert.That(problems).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheRequirementLinksToTheImportedDocumentsResolve()
    {
        string requirements = string.Join('\n', MarkdownFiles.ReadLines("requirements/req_mcp-ui-host.md"));

        List<string> missing = [];
        foreach (string document in _ImportedDocuments)
        {
            string name = Path.GetFileName(document);
            if (requirements.Contains("../docs/" + name, StringComparison.Ordinal) && !File.Exists(RepositoryRoot.Combine(document)))
            {
                missing.Add(name);
            }
        }

        // Each of the four is linked by the requirement list at least once, except those it does not mention.
        bool linksAtLeastOne = _ImportedDocuments.Any(document => requirements.Contains("../docs/" + Path.GetFileName(document), StringComparison.Ordinal));
        await Assert.That(linksAtLeastOne).IsTrue();
        await Assert.That(missing).IsEquivalentTo(Array.Empty<string>(), CollectionOrdering.Matching);
    }

    #endregion
}
