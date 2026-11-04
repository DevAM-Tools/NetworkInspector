// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Writing;

/// <summary>A batch the writer could not write; accounted by the exporter on the caller's thread.</summary>
/// <param name="PacketCount">Items that were in the batch.</param>
/// <param name="FirstItemIndex">Index of the batch's first item, for <see cref="ExportErrorEventArgs.ItemIndex"/>.</param>
/// <param name="EstimatedBytes">The batch's size estimate at the hand-off.</param>
/// <param name="Kind">Error classification (<see cref="ExportErrorKind.IoError"/> for an <see cref="IOException"/>).</param>
/// <param name="Message">Message for the error event, e.g. "Parquet batch write failed, 50000 packets lost: …".</param>
internal readonly record struct LostBatch(int PacketCount, int FirstItemIndex, long EstimatedBytes, ExportErrorKind Kind, string Message);
