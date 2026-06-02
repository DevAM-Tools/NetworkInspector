// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

// CA2007 (ConfigureAwait): disabled file-wide. These are in-process TUnit test
// methods awaiting helper Tasks. TUnit runs tests on its own runner with no
// captured UI/ASP.NET synchronization context, so ConfigureAwait(false) cannot
// change scheduling here — it would only add noise and obscure the assertions.
#pragma warning disable CA2007

namespace NetworkInspector.FrameBuilder.Tests.Negative;

/// <summary>
/// V0 negative-compile tests: each snippet under <c>Negative/Snippets/</c>
/// describes a stack composition that MUST be rejected by the C# compiler
/// because the typed-kind capability constraints introduced in Phase V0 do
/// not match.  The test asserts that compilation produces at least one
/// error diagnostic.
/// </summary>
internal sealed class NegativeCompileTests
{
    /// <summary>Eth -&gt; UDP: outer EthernetLayer does not provide a pseudo-header for the UDP transport.</summary>
    [Test]
    public async Task Eth_then_Udp_does_not_compile()
        => await AssertSnippetFails("Eth_then_Udp_must_not_compile.cs.txt");

    /// <summary>Eth -&gt; TCP: outer EthernetLayer does not provide a pseudo-header for the TCP transport.</summary>
    [Test]
    public async Task Eth_then_Tcp_does_not_compile()
        => await AssertSnippetFails("Eth_then_Tcp_must_not_compile.cs.txt");

    /// <summary>FrameStack.Start with TCP: TCP is not IRootLayer.</summary>
    [Test]
    public async Task Start_with_Tcp_does_not_compile()
        => await AssertSnippetFails("Start_with_Tcp_must_not_compile.cs.txt");

    /// <summary>FrameStack.Start with UDP: UDP is not IRootLayer.</summary>
    [Test]
    public async Task Start_with_Udp_does_not_compile()
        => await AssertSnippetFails("Start_with_Udp_must_not_compile.cs.txt");

    /// <summary>UDP -&gt; UDP: inner UDP requires a pseudo-header that the outer UDP does not provide.</summary>
    [Test]
    public async Task Udp_then_Udp_does_not_compile()
        => await AssertSnippetFails("Udp_then_Udp_must_not_compile.cs.txt");

    /// <summary>SomeIp -&gt; anything: SomeIpLayer is a terminal payload layer (not IInteriorLayer); nothing may sit beneath it.</summary>
    [Test]
    public async Task Stack_onto_payload_layer_does_not_compile()
        => await AssertSnippetFails("Stack_onto_payload_layer_must_not_compile.cs.txt");

    private static async Task AssertSnippetFails(string snippetName)
    {
        IReadOnlyList<Diagnostic> diagnostics = NegativeCompileHarness.Compile(snippetName);
        bool hasError = diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        await Assert.That(hasError)
            .IsTrue()
            .Because($"snippet '{snippetName}' must NOT compile, but produced no error diagnostic. " +
                $"Diagnostics: {string.Join("; ", diagnostics.Select(d => d.ToString()))}");
    }
}
