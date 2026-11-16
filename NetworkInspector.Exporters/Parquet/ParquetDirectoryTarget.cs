// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Parquet;

/// <summary>
/// The target of <see cref="ParquetExporter.Builder.ToDirectory"/>: on the first open it creates the folder and removes the
/// files of an earlier dataset (the six fixed table files and <c>fields/*.parquet</c>), then every open goes to a
/// <see cref="DirectoryExportTarget"/> on the same folder. An export that opens nothing leaves no folder.
/// <para>
/// <b>Thread safety:</b> not thread-safe beyond the guarantee of <see cref="IExportTarget"/>: the exporter calls it
/// sequentially, from the caller's thread and then from its writer thread, never at the same time.
/// </para>
/// </summary>
/// <seealso cref="DirectoryExportTarget"/>
internal sealed class ParquetDirectoryTarget : IExportTarget
{
    #region Configuration and state

    /// <summary>Opens the entries below the folder; also resolves them and keeps them inside it.</summary>
    private readonly DirectoryExportTarget _Inner;

    /// <summary>Whether the folder was created and the earlier dataset cleared; set once, by the first open.</summary>
    private bool _Prepared;

    /// <summary>Creates the target; nothing is touched.</summary>
    /// <param name="fullPath">The full path of the dataset folder.</param>
    internal ParquetDirectoryTarget(string fullPath) => _Inner = new DirectoryExportTarget(fullPath);

    #endregion

    #region Opening

    /// <summary>Prepares the folder on the first call, then opens the entry in it.</summary>
    /// <param name="name">The entry; a named entry of the dataset.</param>
    /// <param name="stream">The write-only entry stream on success.</param>
    /// <param name="failure">The reason on failure: the folder cannot be prepared or the inner target refused the entry.</param>
    /// <returns><see langword="true"/> when <paramref name="stream"/> is set.</returns>
    public bool TryOpenWrite(ExportEntryName name, [NotNullWhen(true)] out Stream? stream, out string failure)
    {
        if (!_Prepared)
        {
            // The clean-up runs once, before the first entry, so a re-export never mixes old and new files.
            _Prepared = true;
            try
            {
                Directory.CreateDirectory(_Inner.RootPath);
                _ClearPriorArtifacts(_Inner.RootPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                stream = null;
                failure = exception.Message;
                return false;
            }
        }

        return _Inner.TryOpenWrite(name, out stream, out failure);
    }

    #endregion

    #region Clearing an earlier dataset

    /// <summary>
    /// Deletes prior Parquet artifacts under <paramref name="rootDirectory"/> so a re-export to
    /// the same path does not leave orphan <c>fields/field_*.parquet</c> files from a previous
    /// run with a different field-ID set. Base files are overwritten when opened
    /// anyway; clearing the fields directory is what makes re-export deterministic. The base files are deleted
    /// as well because some are created conditionally (<c>topology.parquet</c>, <c>catalog.parquet</c>,
    /// <c>protocols.parquet</c>), so a stale copy from an earlier run would otherwise survive next to the new dataset.
    /// </summary>
    /// <param name="rootDirectory">The dataset directory; it exists when the method runs.</param>
    /// <exception cref="IOException">A prior file cannot be deleted.</exception>
    /// <remarks>
    /// Security: this deletes files in a caller-chosen directory. The threat is destroying unrelated data, so the
    /// deletion is restricted to the six fixed dataset file names and to <c>*.parquet</c> files directly inside
    /// <c>fields</c>; no recursion and no other pattern. Rejected alternative: deleting the whole directory, which
    /// would remove foreign files. Accepted residual risk: a user's own <c>fields/*.parquet</c> files in the chosen
    /// directory are removed; the Parquet README documents this as the re-export behaviour.
    /// </remarks>
    private static void _ClearPriorArtifacts(string rootDirectory)
    {
        // The names come from the sink, so the clean-up follows its layout (ParquetBatchSink.FixedEntries, FieldsFolder).
        foreach (ExportEntryName entry in ParquetBatchSink.FixedEntries)
        {
            string path = Path.Combine(rootDirectory, entry.Text);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        string fieldsDirectory = Path.Combine(rootDirectory, ParquetBatchSink.FieldsFolder);
        if (!Directory.Exists(fieldsDirectory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(fieldsDirectory, "*.parquet"))
        {
            File.Delete(file);
        }
    }

    #endregion
}
