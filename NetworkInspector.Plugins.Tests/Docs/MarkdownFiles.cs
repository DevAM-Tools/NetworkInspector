// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Small readers for the Markdown documents under test: the lines outside fenced code blocks, the headings, and the relative
/// links. Stateless; thread-safe.
/// </summary>
internal static partial class MarkdownFiles
{
    #region Public API

    /// <summary>Reads a document of the repository and returns its lines.</summary>
    /// <param name="relativePath">Path below the repository root.</param>
    /// <returns>The lines of the file.</returns>
    internal static string[] ReadLines(string relativePath) => File.ReadAllLines(RepositoryRoot.Combine(relativePath));

    /// <summary>The lines outside fenced code blocks (the fence lines themselves are dropped).</summary>
    /// <param name="lines">All lines of a document.</param>
    /// <returns>The prose lines.</returns>
    internal static List<string> ProseLines(IEnumerable<string> lines)
    {
        List<string> prose = [];
        bool inFence = false;
        foreach (string line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                prose.Add(line);
            }
        }

        return prose;
    }

    /// <summary>The texts of the headings (any level) outside fenced code blocks.</summary>
    /// <param name="lines">All lines of a document.</param>
    /// <returns>The heading texts without the leading <c>#</c> marks.</returns>
    internal static List<string> Headings(IEnumerable<string> lines) =>
        [.. ProseLines(lines).Where(static line => line.StartsWith('#')).Select(static line => line.TrimStart('#').Trim())];

    /// <summary>
    /// The relative link targets of a document: Markdown links outside fenced code blocks and inline code, without address
    /// schemes, without pure fragments, and without the fragment of a file link.
    /// </summary>
    /// <param name="lines">All lines of a document.</param>
    /// <returns>The link targets as written, minus any <c>#fragment</c>.</returns>
    internal static List<string> RelativeLinks(IEnumerable<string> lines)
    {
        List<string> links = [];
        foreach (string line in ProseLines(lines))
        {
            string withoutCode = _InlineCode().Replace(line, string.Empty);
            foreach (System.Text.RegularExpressions.Match match in _LinkTarget().Matches(withoutCode))
            {
                string target = match.Groups[1].Value;
                int fragment = target.IndexOf('#', StringComparison.Ordinal);
                if (fragment >= 0)
                {
                    target = target[..fragment];
                }

                if (target.Length > 0 && !target.Contains("://", StringComparison.Ordinal) && !target.StartsWith("mailto:", StringComparison.Ordinal))
                {
                    links.Add(target);
                }
            }
        }

        return links;
    }

    #endregion

    #region Private Helpers

    [System.Text.RegularExpressions.GeneratedRegex("`[^`]*`")]
    private static partial System.Text.RegularExpressions.Regex _InlineCode();

    [System.Text.RegularExpressions.GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial System.Text.RegularExpressions.Regex _LinkTarget();

    #endregion
}
