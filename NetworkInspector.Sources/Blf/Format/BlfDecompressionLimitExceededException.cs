// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Thrown when a BLF container's uncompressed size exceeds the configured
/// <see cref="BlfSourceOptions.MaxUncompressedContainerSize"/> or
/// <see cref="BlfStreamSource.MaxUncompressedContainerSize"/> limit.
/// </summary>
/// <remarks>
/// <para>
/// This exception is intentionally <b>not</b> derived from <see cref="BlfException"/>
/// so that it propagates through all internal catch blocks that swallow
/// <see cref="BlfException"/> or <see cref="OutOfMemoryException"/> and converts
/// them into skipped-frame events. The limit exceeded condition is a deliberate
/// configuration decision by the caller, not a corrupt-data scenario, and the caller
/// must be given the opportunity to react.
/// </para>
/// <para>
/// Callers that want to treat a limit violation as a skip event can catch this
/// exception and handle it explicitly.
/// </para>
/// <para><b>Thread-safety:</b> Immutable after construction.</para>
/// </remarks>
public sealed class BlfDecompressionLimitExceededException : Exception
{
    /// <summary>
    /// The configured maximum uncompressed container size in bytes.
    /// </summary>
    public long ConfiguredLimit
    {
        get;
    }

    /// <summary>
    /// The uncompressed size in bytes that was requested by the BLF container header.
    /// </summary>
    public long RequestedSize
    {
        get;
    }

    /// <summary>
    /// Creates a new <see cref="BlfDecompressionLimitExceededException"/>.
    /// </summary>
    /// <param name="configuredLimit">Configured limit in bytes.</param>
    /// <param name="requestedSize">Requested uncompressed size in bytes.</param>
    public BlfDecompressionLimitExceededException(long configuredLimit, long requestedSize)
        : base($"BLF container decompression limit exceeded: requested {requestedSize:N0} bytes, configured limit is {configuredLimit:N0} bytes.")
    {
        ConfiguredLimit = configuredLimit;
        RequestedSize = requestedSize;
    }
}
