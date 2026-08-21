// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests.Protocols;

/// <summary>
/// Exit-point coverage for <see cref="ParseContext"/> properties beyond dispatch tests.
/// </summary>
internal sealed class ParseContextTests
{
    [Test]
    public async Task Empty_Context_HasExpectedDefaults()
    {
        ParseContext ctx = ParseContext.Empty;
        bool hasStack = ctx.HasStack;
        bool hasIndex = ctx.HasIndex;
        await Assert.That(hasStack).IsFalse();
        await Assert.That(hasIndex).IsFalse();
    }

    [Test]
    public async Task IndexedContext_HasIndex()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();
        PacketIndex index = new(stack);

        ParseContext ctx = new(index, stack);
        bool hasIndex = ctx.HasIndex;
        Stack? ctxStack = ctx.Stack;
        await Assert.That(hasIndex).IsTrue();
        await Assert.That(ctxStack).IsNotNull();
        await Assert.That(ctxStack!).IsEqualTo(stack);
    }
}
