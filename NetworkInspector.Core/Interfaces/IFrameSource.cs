// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Represents a source of captured network frames (e.g., a live capture device, a pcap file).
/// <para>
/// Implementations feed raw frames into the analysis pipeline via a pull-based model:
/// the consumer calls <see cref="NextFrame"/> in a loop until it returns <c>null</c>.
/// </para>
/// <para>
/// A single source can be used with multiple <see cref="Stack"/> instances for parallel analysis.
/// </para>
/// </summary>
public interface IFrameSource : IDisposable
{
    #region Properties

    /// <summary>Gets the user-friendly display name of the source.</summary>
    string UiName
    {
        get;
    }

    /// <summary>Optional human-readable description shown in the UI.</summary>
    string? Description
    {
        get;
    }

    /// <summary>
    /// Gets an estimate of the total frame count, if known.
    /// <para>
    /// Returns <c>null</c> if the total count is unknown (e.g., live capture).
    /// When <see cref="IsFrameCountTruncated"/> is <c>true</c>, this returns
    /// <see cref="int.MaxValue"/> and the actual file contains more frames
    /// than can be processed.
    /// Used for progress reporting and memory pre-allocation.
    /// </para>
    /// </summary>
    int? EstimatedFrameCount
    {
        get;
    }

    /// <summary>
    /// Indicates whether the source file contains more frames than the
    /// supported maximum of <see cref="int.MaxValue"/>.
    /// <para>
    /// When <c>true</c>, <see cref="EstimatedFrameCount"/> returns
    /// <see cref="int.MaxValue"/> and the source will stop producing
    /// frames after that limit is reached.
    /// </para>
    /// </summary>
    bool IsFrameCountTruncated => false;

    /// <summary>Whether the source is currently capturing.</summary>
    bool IsRunning
    {
        get;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Initializes the source and prepares it for frame reading.
    /// <para>
    /// Use this to open files, start capture devices, register frame interfaces, etc.
    /// After this call, <see cref="NextFrame"/> can be called to retrieve frames.
    /// </para>
    /// </summary>
    /// <param name="sourceId">
    /// The unique identifier assigned to this source during registration
    /// via <see cref="FrameInterfaceRegistry.RegisterSource"/>. Use this ID
    /// when registering interfaces with the registry.
    /// </param>
    /// <param name="registry">
    /// The frame interface registry for registering capture interfaces (e.g., "eth0", "wlan0").
    /// </param>
    void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry);

    /// <summary>
    /// Reads the next frame from the source.
    /// <para>
    /// Returns the next available frame, or <c>null</c> when no more frames are available
    /// (end of file, capture stopped, etc.). Implementations should clean up resources
    /// when returning <c>null</c>.
    /// </para>
    /// </summary>
    /// <returns>
    /// The next <see cref="Frame"/>, or <c>null</c> if the source is exhausted.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The source has not been started via <see cref="Start"/>.
    /// </exception>
    Frame? NextFrame();

    #endregion
}