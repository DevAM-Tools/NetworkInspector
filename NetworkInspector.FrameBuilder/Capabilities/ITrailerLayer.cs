// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Optional trailer that is appended after the payload, after every other
/// post-fix is finished (typical examples: Ethernet FCS, MIC, auth tag).
/// </summary>
public interface ITrailerLayer
{
    /// <summary>Trailer length in bytes.</summary>
    int TrailerSize
    {
        get;
    }

    /// <summary>
    /// Writes the trailer at the end of the frame.  <paramref name="frame"/>
    /// has length <c>payloadEnd + TrailerSize</c>; the bytes from
    /// <c>0</c> to <paramref name="payloadEnd"/> are the data the trailer
    /// applies to.
    /// </summary>
    /// <param name="frame">Frame buffer; the last <see cref="TrailerSize"/> bytes are this trailer's slot.</param>
    /// <param name="payloadEnd">Offset where the payload ends (= where the trailer slot begins).</param>
    void WriteTrailer(Span<byte> frame, int payloadEnd);
}
