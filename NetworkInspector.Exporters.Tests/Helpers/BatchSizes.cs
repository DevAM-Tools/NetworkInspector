// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>Reference values for the size estimates of a <see cref="ColumnarPacketBatch"/>, computed independently of the batch's running totals.</summary>
/// <remarks>Thread safety: stateless; the batch must not be changed while it is read.</remarks>
internal static class BatchSizes
{
    #region Size references

    /// <summary>Sums the estimate of every bag: the reference that the batch's running total of its bags is checked against.</summary>
    /// <param name="batch">The batch to inspect.</param>
    /// <returns>The sum of <see cref="FieldColumnBag.EstimatedSizeBytes"/> over all bags.</returns>
    internal static long SumOfBags(ColumnarPacketBatch batch)
    {
        long total = 0;
        foreach (FieldColumnBag bag in batch.FieldBags.Values)
        {
            total += bag.EstimatedSizeBytes;
        }

        return total;
    }

    #endregion
}
