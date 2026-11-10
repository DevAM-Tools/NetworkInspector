// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the argument validation of <see cref="ProtocolRegistration"/>.
/// </summary>
internal sealed class ProtocolRegistrationArgumentTests
{
    [Test]
    public async Task RegisterStandardProtocolsRejectsANullBuilder()
    {
        await Assert.That(static () => ProtocolRegistration.RegisterStandardProtocols(null!)).Throws<ArgumentNullException>(); // null! is the point of the test
    }

    [Test]
    public async Task RegisterFairProfilingProtocolsRejectsANullBuilder()
    {
        await Assert.That(static () => ProtocolRegistration.RegisterFairProfilingProtocols(null!))
            .Throws<ArgumentNullException>(); // null! is the point of the test
    }
}
