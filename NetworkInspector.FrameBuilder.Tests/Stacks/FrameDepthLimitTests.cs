// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

// Explicit type aliases for the deeply-nested StatelessStack chains used in the
// depth-limit tests.  IDE0008 (TreatWarningsAsErrors) forbids 'var', and the
// cons-list generic cannot be expressed any other way at call sites.  The alias
// RHS must use fully-qualified names because C# alias RHS cannot resolve names
// brought in by 'using' namespace directives (global or file-level).
//
// VStack32 — 1 EthernetLayer + 31 VlanLayer = depth 32 (= FrameSequence.MaxSupportedDepth)
using VStack32 =
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.EthernetLayer,
    NetworkInspector.FrameBuilder.StackEnd>
    >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>;

// VStack33 — 1 EthernetLayer + 32 VlanLayer = depth 33 (= MaxSupportedDepth + 1)
using VStack33 =
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.VlanLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.EthernetLayer,
    NetworkInspector.FrameBuilder.StackEnd>
    >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>;

namespace NetworkInspector.FrameBuilder.Tests.Stacks;

/// <summary>
/// Tests for the cons-list depth limit enforced by
/// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/>.
/// Verifies that stacks at the exact maximum depth (<see cref="FrameSequence{TStack,TTrailer,TInterceptor}.MaxSupportedDepth"/>)
/// succeed and that stacks one layer deeper return
/// <see cref="BuildStatus.StackTooDeep"/> without throwing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VlanLayer"/> is used as the repeating interior layer because it
/// carries no protocol-specific state, can be stacked arbitrarily (QinQ and
/// beyond), and has a fixed 4-byte header that makes total frame sizes
/// predictable.
/// </para>
/// <para>Thread safety: each test is stateless; no shared mutable state.</para>
/// </remarks>
internal sealed class FrameDepthLimitTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);

    /// <summary>
    /// A stack of exactly <see cref="FrameSequence{TStack,TTrailer,TInterceptor}.MaxSupportedDepth"/>
    /// (32) layers must build successfully: <c>MoveNext</c> returns <c>true</c>
    /// and <see cref="BuildStatus.Success"/> is reported.
    /// </summary>
    /// <remarks>
    /// Stack shape: 1 × <see cref="EthernetLayer"/> (root) +
    /// 31 × <see cref="VlanLayer"/> (interior) = depth 32 total.
    /// </remarks>
    [Test]
    public async Task Depth32_MoveNext_Succeeds()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.VlanLayer vlan = new(vlanId: 1);

        // 1 EthernetLayer + 31 VlanLayer = 32 total layers (= MaxSupportedDepth).
        FB.CreatedStack<VStack32, FB.NoTrailer, FB.NoInterceptor> created =
            FB.FrameStack.Start(eth)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan)
                .CreateWithFixedValues();

        // Ethernet (14) + 31 × VlanLayer (4) = 138 bytes; no payload.
        // FrameSequence.MaxSupportedDepth == 32
        byte[] buf = new byte[138];
        FB.FrameSequence<VStack32, FB.NoTrailer, FB.NoInterceptor> seq =
            created.Build(ReadOnlySpan<byte>.Empty);
        bool ok = seq.MoveNext(buf, out int written);
        FB.BuildStatus status = seq.Status;

        await Assert.That(ok).IsTrue();
        await Assert.That(status).IsEqualTo(FB.BuildStatus.Success);
        await Assert.That(written).IsEqualTo(138);
    }

    /// <summary>
    /// A stack one layer beyond the supported maximum (33 layers) must cause
    /// <c>MoveNext</c> to return <c>false</c> with
    /// <see cref="BuildStatus.StackTooDeep"/> — no exception thrown.
    /// </summary>
    /// <remarks>
    /// Stack shape: 1 × <see cref="EthernetLayer"/> +
    /// 32 × <see cref="VlanLayer"/> = depth 33 total (one beyond <c>MaxSupportedDepth</c>).
    /// </remarks>
    [Test]
    public async Task Depth33_MoveNext_ReturnsStackTooDeep()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.VlanLayer vlan = new(vlanId: 1);

        // 1 EthernetLayer + 32 VlanLayer = 33 total layers (MaxSupportedDepth + 1).
        FB.CreatedStack<VStack33, FB.NoTrailer, FB.NoInterceptor> created =
            FB.FrameStack.Start(eth)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan).Then(vlan).Then(vlan).Then(vlan)
                .Then(vlan).Then(vlan)
                .CreateWithFixedValues();

        // Buffer is oversized so BufferTooSmall cannot mask the depth error.
        byte[] buf = new byte[1600];
        FB.FrameSequence<VStack33, FB.NoTrailer, FB.NoInterceptor> seq =
            created.Build(ReadOnlySpan<byte>.Empty);
        bool ok = seq.MoveNext(buf, out int _);
        FB.BuildStatus status = seq.Status;

        await Assert.That(ok).IsFalse();
        await Assert.That(status).IsEqualTo(FB.BuildStatus.StackTooDeep);
    }
}
