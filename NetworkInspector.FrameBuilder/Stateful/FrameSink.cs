// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Per-frame output sink for emission helpers that produce more than one
/// frame per call (TCP handshake, MSS-segmented stream writes, FIN
/// teardown).  The sink is invoked once per emitted frame, in wire order.
/// </summary>
/// <remarks>
/// <para>
/// The supplied <see cref="ReadOnlySpan{T}"/> is only valid for the
/// duration of the call — implementations must copy out any bytes they
/// need to retain (e.g. into a PCAP writer, a list of byte arrays, or an
/// <see cref="System.Buffers.IBufferWriter{T}"/>).  No allocation occurs
/// inside the FrameBuilder for the sink contract itself.
/// </para>
/// <para>
/// Typical adapters in test code:
/// <list type="bullet">
///   <item><c>FrameSink toList = frame =&gt; list.Add(frame.ToArray());</c></item>
///   <item><c>FrameSink toPcap = frame =&gt; pcapWriter.AppendFrame(frame);</c></item>
///   <item><c>FrameSink toBuffer = frame =&gt; { frame.CopyTo(buf.GetSpan(frame.Length)); buf.Advance(frame.Length); };</c></item>
/// </list>
/// </para>
/// </remarks>
/// <param name="frame">The fully serialized wire frame; valid only during the call.</param>
public delegate void FrameSink(ReadOnlySpan<byte> frame);
