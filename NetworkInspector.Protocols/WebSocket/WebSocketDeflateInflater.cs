// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using IO = System.IO;

namespace NetworkInspector.Protocols;

public sealed partial class WebSocketProtocol
{
    #region Per-message DEFLATE decompression (RFC 7692)

    /// <summary>
    /// Decompresses a WebSocket per-message DEFLATE payload (RFC 7692 §7.2.2).
    /// </summary>
    /// <remarks>
    /// <para><b>Algorithm.</b> RFC 7692 §7.2.2 requires that the receiver append the
    /// four-byte sequence <c>0x00 0x00 0xFF 0xFF</c> to the compressed payload before
    /// feeding it into the raw DEFLATE decompressor.  These bytes form a SYNC flush marker
    /// that signals end-of-stream to the <see cref="DeflateStream"/>.</para>
    /// <para>The method returns <see langword="null"/> on decompression failure rather than
    /// propagating an exception — the caller (<see cref="PopulateWebSocketFields"/>) silently
    /// omits the decompressed field in that case, which is preferable to aborting the entire
    /// frame parse.</para>
    /// </remarks>
    private static ReadOnlyMemory<byte>? DecompressPermessageDeflate(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        try
        {
            // Append 0x00 0x00 0xFF 0xFF SYNC flush marker before decompressing.
            byte[] compressedWithTrailer = new byte[data.Length + 4];
            data.Span.CopyTo(compressedWithTrailer);
            compressedWithTrailer[data.Length] = 0x00;
            compressedWithTrailer[data.Length + 1] = 0x00;
            compressedWithTrailer[data.Length + 2] = 0xFF;
            compressedWithTrailer[data.Length + 3] = 0xFF;

            using IO.MemoryStream input = new(compressedWithTrailer, writable: false);
            using DeflateStream deflate = new(input, CompressionMode.Decompress, leaveOpen: true);
            using IO.MemoryStream output = new();
            deflate.CopyTo(output);
            return output.ToArray();
        }
        catch (IO.InvalidDataException)
        {
            return null; // Corrupted DEFLATE stream
        }
        catch (IO.IOException)
        {
            return null; // I/O error during decompression
        }
    }

    #endregion
}
