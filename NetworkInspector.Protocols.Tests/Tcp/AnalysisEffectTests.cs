// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using AnalysisEffect = NetworkInspector.Protocols.TcpProtocol.AnalysisEffect;
using AnalysisEffectFlags = NetworkInspector.Protocols.TcpProtocol.AnalysisEffectFlags;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// The recorded TCP analysis effect keeps <c>double</c> timings and prints its record text with the invariant culture.
/// </summary>
[NotInParallel("current-culture")]
internal sealed class AnalysisEffectTests
{
    #region Helpers

    private static AnalysisEffect _Effect(double initialRtt, double ackRtt) =>
        new(
            StreamIndex: 3,
            Flags: AnalysisEffectFlags.None,
            DupAckNum: 0,
            BytesInFlight: 10,
            InitialRtt: initialRtt,
            AckRtt: ackRtt,
            TimeRelative: 0.015001234,
            TimeDelta: 0.000001,
            ScaledWindowSize: 65535,
            WindowScaleFactor: -1,
            Phase: 2,
            NoIpLayer: false,
            RelativeSeq: 1,
            RelativeAck: 1,
            SequenceIsRelative: true,
            AckIsRelative: true,
            StreamLimitReached: false);

    #endregion

    #region Tests

    [Test]
    public async Task ToStringUsesTheInvariantCultureOnAGermanThread()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        string text;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            text = _Effect(0.025, double.PositiveInfinity).ToString();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        await Assert.That(text).StartsWith("AnalysisEffect { StreamIndex = 3, ");
        await Assert.That(text).Contains("InitialRtt = 0.025, AckRtt = Infinity, TimeRelative = 0.015001234, TimeDelta = 1E-06, ");
        await Assert.That(text).EndsWith("AckIsRelative = True, StreamLimitReached = False }");
    }

    [Test]
    public async Task TimingsKeepDoublePrecision()
    {
        AnalysisEffect effect = _Effect(0.1, double.NaN);

        await Assert.That(effect.InitialRtt).IsEqualTo(0.1);
        await Assert.That(effect.TimeRelative).IsEqualTo(0.015001234);
        await Assert.That(double.IsNaN(effect.AckRtt)).IsTrue();
    }

    #endregion
}
