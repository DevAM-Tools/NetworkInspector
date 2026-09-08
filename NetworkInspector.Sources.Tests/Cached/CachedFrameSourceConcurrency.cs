// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Cached;

/// <summary>
/// Shared overlap of <see cref="CachedFrameSource.NextFrame"/> with concurrent
/// <see cref="CachedFrameSource.FrameById"/> (single writer, multi-reader).
/// </summary>
internal static class CachedFrameSourceConcurrency
{
    #region Public API

    /// <summary>
    /// Writer publishes ids 0..N-1 via NextFrame; readers spin until each id is published then FrameById.
    /// Payload comparison uses a clone taken at NextFrame so a torn publish cannot match by aliasing
    /// the same <see cref="Frame.Data"/> buffer as the writer snapshot.
    /// </summary>
    internal static async Task AssertReadDuringWrite(CachedFrameSource source, int count)
    {
        SharedState state = new();
        Frame[] snapshots = new Frame[count];
        byte[][] payloadClones = new byte[count][];

        Thread writer = new(() =>
        {
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Frame? next = source.NextFrame();
                    if (next is null)
                    {
                        throw new InvalidOperationException("NextFrame returned null before the batch ended.");
                    }

                    snapshots[i] = next.Value;
                    payloadClones[i] = next.Value.Data.ToArray();
                    state.Published = i;
                }
            }
            catch (Exception ex)
            {
                state.Fault = ex;
            }
        })
        {
            Name = "cached-frame-writer",
        };
        writer.Start();

        List<Task> readers = [];
        for (int t = 0; t < 4; t++)
        {
            readers.Add(Task.Run(() =>
            {
                SpinWait spinner = default;
                for (int i = 0; i < count; i++)
                {
                    while (state.Published < i)
                    {
                        if (state.Fault is not null)
                        {
                            return;
                        }

                        spinner.SpinOnce();
                    }

                    Frame? cached = source.FrameById(new FrameId(i));
                    if (cached is null)
                    {
                        throw new InvalidOperationException($"FrameById returned null for published id {i}.");
                    }

                    Frame expected = snapshots[i];
                    byte[] expectedPayload = payloadClones[i];
                    if (!cached.Value.Data.Span.SequenceEqual(expectedPayload))
                    {
                        throw new InvalidOperationException($"Payload mismatch at id {i}.");
                    }

                    if (cached.Value.Timestamp != expected.Timestamp
                        || cached.Value.LinkType != expected.LinkType
                        || cached.Value.InterfaceId != expected.InterfaceId)
                    {
                        throw new InvalidOperationException($"Metadata mismatch at id {i}.");
                    }
                }
            }));
        }

        await Task.WhenAll(readers).ConfigureAwait(false);
        writer.Join();

        await Assert.That(state.Fault).IsNull();
    }

    #endregion

    #region Nested types

    private sealed class SharedState
    {
        public volatile int Published = -1;
        public volatile Exception? Fault;
    }

    #endregion
}
