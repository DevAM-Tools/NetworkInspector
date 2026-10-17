// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Helpers;

/// <summary>
/// A capture written to a unique file in the temp directory, deleted on dispose. For tests that must go through the
/// file backends (<c>Open</c>, memory mapping) instead of <c>FromData</c>.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class TempCaptureFile : IDisposable
{
    /// <summary>Writes <paramref name="data"/> to a new temp file with the given extension (e.g. <c>".pcapng"</c>).</summary>
    internal TempCaptureFile(byte[] data, string extension)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ni-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(Path, data);
    }

    /// <summary>Absolute path of the file.</summary>
    internal string Path
    {
        get;
    }

    /// <summary>Deletes the file. A source that still maps it must be disposed first.</summary>
    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // A leftover temp file does not affect the test result; the OS temp cleanup removes it.
        }
    }
}
