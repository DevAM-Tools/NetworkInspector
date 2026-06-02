// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Abstract base for profiling scenarios that export a pre-generated batch of items
/// (<typeparamref name="TItem"/> = <see cref="Frame"/> or <see cref="Packet"/>) into a
/// <see cref="MemoryStream"/> on every <see cref="Run"/> call.
///
/// <para>
/// The base class handles the boilerplate of item generation, stream management, and
/// disposal. Subclasses supply the format-specific exporter creation and export call
/// via <see cref="Export"/>, and item construction via <see cref="CreateItems"/>.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety.</b> Not thread-safe. <see cref="Setup"/>, <see cref="Run"/>,
/// and <see cref="Cleanup"/> must be called sequentially from the same thread.</para>
/// <para><b>Lifecycle invariant.</b> <see cref="Setup"/> must be called exactly once before
/// any call to <see cref="Run"/>. Calling <see cref="Run"/> before <see cref="Setup"/> or
/// after <see cref="Cleanup"/> is undefined behaviour.</para>
/// </remarks>
/// <typeparam name="TItem">The item type exported per iteration: <see cref="Frame"/> or <see cref="Packet"/>.</typeparam>
internal abstract class ExportScenarioBase<TItem> : IProfilingScenario, IDisposable
{
    /// <summary>The stack used to generate frames; allocated in <see cref="Setup"/>, disposed in <see cref="Dispose"/>.</summary>
    protected Stack? _Stack;

    /// <summary>Pre-generated export items; allocated in <see cref="Setup"/>, cleared in <see cref="Dispose"/>.</summary>
    protected TItem[]? _Items;

    /// <summary>Reusable output stream; allocated in <see cref="Setup"/>, disposed in <see cref="Dispose"/>.</summary>
    protected MemoryStream? _Stream;

    /// <summary>Number of items exported per <see cref="Run"/> call.</summary>
    protected abstract int BatchSize { get; }

    /// <summary>Initial capacity of the reusable <see cref="MemoryStream"/> in bytes.</summary>
    protected abstract int InitialStreamCapacityBytes { get; }

    /// <summary>
    /// Constructs the typed item array from the given <paramref name="stack"/> and raw <paramref name="frames"/>.
    /// Frame-based scenarios return <paramref name="frames"/> directly; packet-based scenarios
    /// parse and materialise.
    /// </summary>
    protected abstract TItem[] CreateItems(Stack stack, Frame[] frames);

    /// <summary>
    /// Runs a single export iteration: resets the stream and exports all <paramref name="items"/>
    /// using the format-specific exporter.
    /// </summary>
    protected abstract void Export(MemoryStream stream, TItem[] items);

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Description { get; }

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => BatchSize;

    /// <inheritdoc/>
    public abstract string WorkUnitName { get; }

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        Frame[] frames = FrameHelper.CreateSharedFrames(BatchSize, _Stack);
        _Items = CreateItems(_Stack, frames);
        _Stream = new MemoryStream(InitialStreamCapacityBytes);
    }

    /// <inheritdoc/>
    public void Run()
    {
        MemoryStream ms = _Stream!;
        ms.SetLength(0);
        Export(ms, _Items!);
    }

    /// <inheritdoc/>
    public void Cleanup() => Dispose();

    /// <inheritdoc/>
    public void Dispose()
    {
        _Stream?.Dispose();
        _Stream = null;
        _Stack?.Dispose();
        _Stack = null;
        _Items = null;
    }
}
