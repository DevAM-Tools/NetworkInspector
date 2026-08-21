// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Shared <c>--filter</c> handling for the CLI commands: compiling an expression against the
/// protocol stack and evaluating it per packet, reporting failures on stderr instead of throwing.
/// </summary>
internal static class CliFilter
{
    #region Public API

    /// <summary>
    /// <see langword="true"/> when <paramref name="expression"/> asks for actual filtering. An
    /// absent or blank expression leaves a command on its unfiltered path, which for
    /// <c>ni convert</c> means no protocol stack is built and no frame is ever parsed.
    /// </summary>
    internal static bool IsActive([NotNullWhen(true)] string? expression) =>
        !string.IsNullOrWhiteSpace(expression);

    /// <summary>
    /// Compiles <paramref name="expression"/> against <paramref name="stack"/>. On failure the
    /// compile error is written to stderr and <see langword="false"/> is returned, so the caller
    /// can exit with <see cref="ExitCode.ArgumentError"/>.
    /// </summary>
    internal static bool TryCompile(
        string expression,
        Stack stack,
        [NotNullWhen(true)] out IFilter? filter)
    {
        FilterResult<PacketFilter> compiled = PacketFilter.Compile(expression, stack);
        if (!compiled.TryGetValue(out PacketFilter? compiledFilter))
        {
            Console.Error.WriteLine($"Error: invalid --filter expression: {compiled.Error.Message}");
            filter = null;
            return false;
        }

        filter = compiledFilter;
        return true;
    }

    /// <summary>
    /// Evaluates <paramref name="filter"/> against <paramref name="packet"/>.
    /// Pass <paramref name="index"/> when the packet was parsed with
    /// <see cref="Packet.ParseFrameIndexed(PacketId, Stack, Frame, PacketIndex)"/> so protocol
    /// presence stays O(1). Pass <see cref="PacketIndex"/> or <see cref="PacketIndexReaderView"/>
    /// as <typeparamref name="TIndex"/> — do not cast a view to <see cref="IPacketIndexReader"/>
    /// (that boxes).
    /// On an evaluation failure the reason is written to stderr and <see langword="false"/> is
    /// returned; the caller must abort, because a filter that cannot decide would otherwise
    /// silently drop or keep data.
    /// </summary>
    internal static bool TryMatch<TIndex>(
        IFilter filter,
        Packet packet,
        TIndex? index,
        out bool matched)
        where TIndex : IPacketIndexReader
    {
        if (filter.TryIsMatch(packet, index, out matched, out FilterError? failure))
        {
            return true;
        }

        Console.Error.WriteLine($"Error: filter evaluation failed: {failure.Message}");
        matched = false;
        return false;
    }

    /// <summary>Usage lines describing <c>--filter</c>, shared by both commands.</summary>
    internal static void PrintUsageLines()
    {
        Console.Error.WriteLine("  --filter <expr>       Only keep packets matching this filter expression");
    }

    #endregion
}
