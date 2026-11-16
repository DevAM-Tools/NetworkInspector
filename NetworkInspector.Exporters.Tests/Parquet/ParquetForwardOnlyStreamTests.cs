// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Parquet;

/// <summary>
/// Evidence for the decision to write Parquet datasets onto host streams: Parquet.Net documents its output as a "writeable,
/// seekable stream", and this class proves that a forward-only stream is enough. A red result here means the Parquet exporter
/// cannot write to a stream that cannot seek or report its position.
/// </summary>
/// <seealso cref="ForwardOnlyStream"/>
[Category("Slow")]
internal sealed class ParquetForwardOnlyStreamTests
{
    /// <summary>Number of rows in each of the two row groups of the proof file.</summary>
    private const int _RowsPerGroup = 3;

    /// <summary>The text column of each row group; never mutated.</summary>
    private static readonly string[] _Texts = ["a", "b", "c"];

    /// <summary>
    /// Writes a two-column file with two row groups through <see cref="ParquetWriter"/> onto a <see cref="ForwardOnlyStream"/>,
    /// the way the exporter's file writer does (zstd, optimal level), and reads it back.
    /// </summary>
    /// <returns>The task of the test.</returns>
    [Test]
    public async Task ParquetNetWritesAReadableFileToAForwardOnlyStream()
    {
        DataField<int> id = new("id");
        DataField<string> text = new("text");
        ParquetSchema schema = new(id, text);
        ParquetOptions options = new()
        {
            CompressionMethod = CompressionMethod.Zstd,
            CompressionLevel = System.IO.Compression.CompressionLevel.Optimal,
        };
        ForwardOnlyStream sink = new();

        // Write two row groups; the writer's dispose appends the footer. Any Seek, Read, Length, or Position access of
        // Parquet.Net on the caller's stream throws NotSupportedException and fails the test with the member's name.
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, sink, options))
        {
            for (int group = 0; group < 2; group++)
            {
                using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
                int first = group * _RowsPerGroup;
                await rowGroup.WriteAsync<int>(id, new[] { first, first + 1, first + 2 });
                await rowGroup.WriteAsync(text, _Texts);
            }
        }

        // Read the bytes back through an ordinary seekable stream: the file must be complete and valid.
        using MemoryStream readable = new(sink.ToArray());
        await using ParquetReader reader = await ParquetReader.CreateAsync(readable);
        List<int> ids = [];
        List<string> texts = [];
        for (int group = 0; group < reader.RowGroupCount; group++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);
            int[] idBuffer = new int[rowGroup.RowCount];
            string[] textBuffer = new string[rowGroup.RowCount];
            await rowGroup.ReadAsync<int>(id, idBuffer, null, CancellationToken.None);
            await rowGroup.ReadAsync(text, textBuffer, null, CancellationToken.None);
            ids.AddRange(idBuffer);
            texts.AddRange(textBuffer);
        }

        await Assert.That(reader.RowGroupCount).IsEqualTo(2);
        await Assert.That(string.Join(',', ids)).IsEqualTo("0,1,2,3,4,5");
        await Assert.That(string.Join(',', texts)).IsEqualTo("a,b,c,a,b,c");
    }
}
