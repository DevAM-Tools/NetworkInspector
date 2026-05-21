// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="Stack"/> lifecycle: Shutdown idempotency and Dispose safety
/// (regression for HIGH-2).
/// </summary>
internal sealed class StackTests
{
    private static Stack BuildStack(params IProtocol[] protocols)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        foreach (IProtocol protocol in protocols)
        {
            builder.RegisterProtocol(protocol);
        }
        return builder.Build();
    }

    // === Shutdown idempotency (regression for HIGH-2) ===

    [Test]
    public async Task Shutdown_CalledTwice_InvokesOnShutdownOnce()
    {
        // Regression for HIGH-2: without the _ShutdownFlag once-gate, calling Shutdown
        // twice would invoke OnShutdown twice on every registered protocol.
        CountingProto proto = new();
        using Stack stack = BuildStack(proto);

        stack.Shutdown();
        stack.Shutdown(); // must be a no-op

        await Assert.That(proto.ShutdownCount).IsEqualTo(1);
    }

    [Test]
    public async Task Shutdown_ThenDispose_InvokesOnShutdownOnce()
    {
        // Calling Shutdown explicitly and then letting Dispose run via `using` must still
        // invoke OnShutdown exactly once.
        CountingProto proto = new();
        using Stack stack = BuildStack(proto);

        stack.Shutdown();
        // Dispose is called by `using` — it calls Shutdown again internally.

        await Assert.That(proto.ShutdownCount).IsEqualTo(1);
    }

    [Test]
    public async Task Shutdown_WithNoProtocols_DoesNotThrow()
    {
        // An empty stack should shutdown without error.
        using Stack stack = BuildStack();
        Exception? ex = null;
        try
        {
            stack.Shutdown();
        }
        catch (Exception e)
        {
            ex = e;
        }
        await Assert.That(ex).IsNull();
    }

    // === Helpers ===

    private sealed class CountingProto : IProtocol
    {
        /// <summary>Number of times <see cref="OnShutdown"/> has been called.</summary>
        public int ShutdownCount;

        public string Name => "counting";
        public string UiName => "Counting";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;

        public void OnShutdown(Stack stack) => ShutdownCount++;
    }
}
