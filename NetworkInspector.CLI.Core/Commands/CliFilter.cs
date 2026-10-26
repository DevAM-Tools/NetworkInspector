// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Shared <c>--filter</c> handling for the CLI commands: compiling an expression against the
/// protocol stack and evaluating it per packet, reporting failures on the error writer instead of throwing. The helpers are
/// stateless and thread-safe; a compiled filter is not (it owns evaluation scratch space and a per-packet verdict cache), so the
/// one loop of a command that owns a filter must be the only thread that calls it.
/// </summary>
internal static class CliFilter
{
    #region Public API

    /// <summary>
    /// <see langword="true"/> when <paramref name="expression"/> asks for actual filtering. An
    /// absent or blank expression leaves a command on its unfiltered path, which for
    /// <c>ni convert</c> means no frame is ever parsed: the conversion stays at frame level (the stack is built anyway, because the
    /// exporter request carries it).
    /// </summary>
    /// <param name="expression">The value of <c>--filter</c>, or <see langword="null"/> when the option was not given.</param>
    /// <returns><see langword="true"/> when the expression is not blank.</returns>
    internal static bool IsActive([NotNullWhen(true)] string? expression) =>
        !string.IsNullOrWhiteSpace(expression);

    /// <summary>
    /// Compiles <paramref name="expression"/> against <paramref name="stack"/>. On failure the compile error is written as one line to
    /// <paramref name="error"/> and <see langword="false"/> is returned, so the caller can exit with <see cref="ExitCode.ArgumentError"/>.
    /// Commands compile before they open any source or output, so a bad expression costs nothing and touches no file.
    /// </summary>
    /// <param name="expression">The filter expression.</param>
    /// <param name="stack">The stack whose protocols and fields the expression refers to.</param>
    /// <param name="error">Receives the one error line.</param>
    /// <param name="filter">The compiled filter; <see langword="null"/> on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="filter"/> is set.</returns>
    internal static bool TryCompile(
        string expression,
        Stack stack,
        TextWriter error,
        [NotNullWhen(true)] out IFilter? filter)
    {
        FilterResult<PacketFilter> compiled = PacketFilter.Compile(expression, stack);
        if (!compiled.TryGetValue(out PacketFilter? compiledFilter))
        {
            error.WriteLine($"Error: invalid --filter expression: {compiled.Error.Message}");
            filter = null;
            return false;
        }

        filter = compiledFilter;
        return true;
    }

    /// <summary>
    /// Evaluates <paramref name="filter"/> against <paramref name="packet"/>.
    /// Pass <paramref name="index"/> when the packet was parsed with
    /// <see cref="Packet.TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/> so protocol
    /// presence stays O(1). Pass <see cref="PacketIndex"/> or <see cref="PacketIndexReaderView"/>
    /// as <typeparamref name="TIndex"/> — do not cast a view to <see cref="IPacketIndexReader"/>
    /// (that boxes).
    /// On an evaluation failure the reason is written to <paramref name="error"/> and <see langword="false"/> is
    /// returned; the caller must abort, because a filter that cannot decide would otherwise
    /// silently drop or keep data.
    /// </summary>
    /// <typeparam name="TIndex">
    /// The concrete index type, kept generic so the call is not boxed per packet (a struct view passed as an interface would
    /// allocate on every call of the packet loop).
    /// </typeparam>
    /// <param name="filter">The compiled filter.</param>
    /// <param name="packet">The packet to judge.</param>
    /// <param name="index">The protocol index of the loop, or <see langword="null"/> when the packet was parsed without one.</param>
    /// <param name="error">Receives the evaluation failure as one line.</param>
    /// <param name="matched">Whether the packet matches; <see langword="false"/> when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the filter produced a verdict.</returns>
    internal static bool TryMatch<TIndex>(
        IFilter filter,
        Packet packet,
        TIndex? index,
        TextWriter error,
        out bool matched)
        where TIndex : IPacketIndexReader
    {
        if (filter.TryIsMatch(packet, index, out matched, out FilterError? failure))
        {
            return true;
        }

        error.WriteLine($"Error: filter evaluation failed: {failure.Message}");
        matched = false;
        return false;
    }

    /// <summary>Prints the usage line of the filter option to <paramref name="error"/>.</summary>
    /// <param name="error">The diagnostics writer.</param>
    internal static void PrintUsageLines(TextWriter error)
    {
        error.WriteLine("  --filter <expr>       Only keep packets matching this filter expression");
    }

    #endregion
}
