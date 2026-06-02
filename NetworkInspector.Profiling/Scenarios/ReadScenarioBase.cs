// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Abstract base for profiling scenarios that read all frames from a pre-generated
/// sample file on every <see cref="Run"/> call.
///
/// <para>
/// The base class handles frame generation, temp-file lifecycle, and disposal.
/// Subclasses supply the format-specific file creation and iteration logic via
/// <see cref="CreateSampleFile"/> and <see cref="RunIteration"/>.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety.</b> Not thread-safe. <see cref="Setup"/>, <see cref="Run"/>,
/// and <see cref="Cleanup"/> must be called sequentially from the same thread.</para>
/// </remarks>
internal abstract class ReadScenarioBase : IProfilingScenario
{
    /// <summary>The stack used to generate sample frames; allocated in <see cref="Setup"/>, disposed in <see cref="Cleanup"/>.</summary>
    protected Stack? _Stack;

    /// <summary>Path to the sample file written in <see cref="Setup"/>; cleared in <see cref="Cleanup"/>.</summary>
    protected string? _FilePath;

    /// <summary>Number of frames written to and read from the sample file per iteration.</summary>
    protected abstract int FrameCount { get; }

    /// <summary>
    /// Writes a sample file containing <paramref name="frames"/> to disk and returns
    /// the absolute path to the created file.
    /// </summary>
    protected abstract string CreateSampleFile(Frame[] frames);

    /// <summary>
    /// Reads and fully iterates all frames from the file at <paramref name="filePath"/>,
    /// exercising the format-specific source path.
    /// </summary>
    protected abstract void RunIteration(string filePath);

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Description { get; }

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        Frame[] frames = FrameHelper.CreateSharedFrames(FrameCount, _Stack);
        _FilePath = CreateSampleFile(frames);
    }

    /// <inheritdoc/>
    public void Run() => RunIteration(_FilePath!);

    /// <inheritdoc/>
    public void Cleanup()
    {
        SampleFileHelper.Cleanup();
        _Stack?.Dispose();
        _Stack = null;
        _FilePath = null;
    }
}
