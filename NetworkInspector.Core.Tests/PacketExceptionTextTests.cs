// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// The text recorded for a caught protocol exception: the message alone, or the message, a newline, and the stack trace.
/// </summary>
internal sealed class PacketExceptionTextTests
{
    private static InvalidOperationException _Thrown()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [Test]
    public async Task WithStackTraceYieldsMessageNewlineAndStackTrace()
    {
        InvalidOperationException ex = _Thrown();

        string text = Packet.BuildCaughtExceptionText(ex, includeStackTrace: true);

        await Assert.That(ex.StackTrace).IsNotNull();
        await Assert.That(text).IsEqualTo("boom" + "\n" + ex.StackTrace);
    }

    [Test]
    public async Task ExceptionThatWasNeverThrownYieldsTheMessageOnly()
    {
        InvalidOperationException ex = new("never thrown");

        string text = Packet.BuildCaughtExceptionText(ex, includeStackTrace: true);

        await Assert.That(ex.StackTrace).IsNull();
        await Assert.That(text).IsEqualTo("never thrown");
    }

    [Test]
    public async Task WithoutStackTraceFlagYieldsTheMessageOnly()
    {
        InvalidOperationException ex = _Thrown();

        string text = Packet.BuildCaughtExceptionText(ex, includeStackTrace: false);

        await Assert.That(text).IsEqualTo("boom");
    }
}
