// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Read-only statistics for an exporter.
/// <para>
/// <b>Thread safety:</b> Not thread-safe. Properties must be read from the same thread
/// that drives the export. Statistics are valid to inspect after <c>OnFinish()</c> returns.
/// </para>
/// </summary>
public interface IExporterStatistics
{
    #region Properties

    /// <summary>Number of frames/packets successfully written.</summary>
    long WrittenCount
    {
        get;
    }

    /// <summary>Number of frames/packets skipped due to errors or unsupported types.</summary>
    long SkippedCount
    {
        get;
    }

    /// <summary>Number of errors encountered (may be greater than <see cref="SkippedCount"/>).</summary>
    long ErrorCount
    {
        get;
    }

    /// <summary>Whether the exporter has encountered at least one error.</summary>
    bool HasErrors
    {
        get;
    }

    /// <summary>Whether the exporter has finished (<c>OnFinish</c> was called).</summary>
    bool IsFinished
    {
        get;
    }

    #endregion
}