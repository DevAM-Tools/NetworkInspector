// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>Outcome of <see cref="BoundedDecompressor.TryDecompress"/>.</summary>
internal enum DecompressionStatus
{
    /// <summary>The whole input was inflated within the limit.</summary>
    Succeeded,

    /// <summary>The input is not valid compressed data, or reading it failed.</summary>
    Failed,

    /// <summary>The output would exceed the limit; inflating stopped and no output is returned.</summary>
    LimitExceeded
}

/// <summary>
/// Inflates a compressed stream into memory while enforcing an upper bound on the output size.
/// <para>
/// Limit (decompression bomb): a few kilobytes of crafted deflate, gzip or brotli data expand to many gigabytes. The output
/// is therefore produced in small chunks and the first chunk that would push the total over the limit stops the work, before
/// anything more is inflated. Constant or configurable: the caller passes the limit; the protocols read it from the
/// <c>http.max_decompressed_bytes</c> and <c>ws.max_decompressed_bytes</c> settings. Secure by default (64 MiB), because the
/// input comes from untrusted network peers. A body above the limit is reported as <see cref="DecompressionStatus.LimitExceeded"/>
/// and never truncated. Residual risk: an operator who raises the limit to the 128 GiB maximum accepts that a single hostile
/// body can use all that time; the effective bound is still <see cref="Array.MaxLength"/> because the result is one array.
/// </para>
/// <para><b>Thread safety:</b> stateless; every call uses its own buffers.</para>
/// </summary>
internal static class BoundedDecompressor
{
    /// <summary>
    /// Size of the read buffer in bytes. Constant: a bounded scratch buffer (rented from the shared pool) so memory use
    /// during inflating is the output itself plus this chunk, never the decompressor's whole expansion at once.
    /// </summary>
    private const int _ReadChunkSize = 16 * 1024;

    /// <summary>
    /// Reads <paramref name="decompressor"/> to its end and returns the bytes, or stops as soon as the output would be
    /// larger than <paramref name="maxOutputBytes"/>.
    /// </summary>
    /// <param name="decompressor">The decompressing stream. Read only; the caller disposes it.</param>
    /// <param name="maxOutputBytes">The largest output in bytes that is accepted (an output of exactly this size is accepted).</param>
    /// <param name="output">The inflated bytes on success (may be empty); empty otherwise. The memory is owned by the caller.</param>
    /// <returns>
    /// <see cref="DecompressionStatus.Succeeded"/>, <see cref="DecompressionStatus.Failed"/> for invalid compressed data, or
    /// <see cref="DecompressionStatus.LimitExceeded"/> when the output is larger than the limit or than a single array can hold.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="decompressor"/> is <see langword="null"/>.</exception>
    internal static DecompressionStatus TryDecompress(
        System.IO.Stream decompressor, ulong maxOutputBytes, out ReadOnlyMemory<byte> output)
    {
        ArgumentNullException.ThrowIfNull(decompressor);
        output = default;

        // The result is one array, so no setting can raise the bound above the array maximum. Beyond it the output is
        // reported as over the limit rather than failing inside the stream with an unrelated I/O error.
        ulong effectiveLimit = Math.Min(maxOutputBytes, (ulong)Array.MaxLength);

        byte[] chunk = ArrayPool<byte>.Shared.Rent(_ReadChunkSize);
        try
        {
            using System.IO.MemoryStream sink = new();
            ulong total = 0;
            while (true)
            {
                int read = decompressor.Read(chunk, 0, chunk.Length);
                if (read == 0)
                {
                    break;
                }

                // Check before writing: the total is compared as an unsigned sum of at most the limit plus one chunk, so
                // it cannot overflow, and an over-limit chunk is never stored.
                total += (uint)read;
                if (total > effectiveLimit)
                {
                    return DecompressionStatus.LimitExceeded;
                }

                sink.Write(chunk, 0, read);
            }

            // The internal buffer is handed out without a copy: the stream is not used afterwards, and the unused tail of the
            // array is not part of the returned memory.
            output = new ReadOnlyMemory<byte>(sink.GetBuffer(), 0, (int)sink.Length);
            return DecompressionStatus.Succeeded;
        }
        // Malformed input from the wire is an expected failure, not a bug: report it instead of throwing out of the parse.
        catch (System.IO.InvalidDataException)
        {
            return DecompressionStatus.Failed;
        }
        catch (System.IO.IOException)
        {
            return DecompressionStatus.Failed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
