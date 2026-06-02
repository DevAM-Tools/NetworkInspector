// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Minimal shared base for FrameBuilder profiling scenarios that all maintain a
/// pre-allocated payload buffer and a reusable output frame buffer.
///
/// <para>
/// Subclasses declare their <see cref="PayloadSize"/> and call
/// <see cref="InitializeBuffers"/> at the start of <see cref="IProfilingScenario.Setup"/>
/// to size <see cref="_Payload"/>. <see cref="_Buffer"/> is sized by the subclass
/// once the stack header size is known.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety.</b> Not thread-safe; Setup, Run, and Cleanup must be called
/// sequentially from the same thread.</para>
/// </remarks>
internal abstract class FrameBuilderScenarioBase : IProfilingScenario
{
    /// <summary>Pre-allocated payload buffer; sized to <see cref="PayloadSize"/> by <see cref="InitializeBuffers"/>.</summary>
    protected byte[] _Payload = [];

    /// <summary>Output frame buffer; sized by the subclass in <see cref="IProfilingScenario.Setup"/> after the stack header size is known.</summary>
    protected byte[] _Buffer = [];

    /// <summary>Payload size in bytes for each frame build.</summary>
    protected abstract int PayloadSize { get; }

    /// <summary>
    /// Allocates <see cref="_Payload"/> to <see cref="PayloadSize"/> bytes.
    /// Must be called at the start of <see cref="IProfilingScenario.Setup"/>.
    /// </summary>
    protected void InitializeBuffers() => _Payload = new byte[PayloadSize];

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Description { get; }

    /// <inheritdoc/>
    public abstract long WorkUnitsPerIteration { get; }

    /// <inheritdoc/>
    public abstract string WorkUnitName { get; }

    /// <inheritdoc/>
    public abstract void Setup();

    /// <inheritdoc/>
    public abstract void Run();

    /// <inheritdoc/>
    public abstract void Cleanup();
}
