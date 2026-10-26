// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// An exception from foreign code that misbehaves while it is described: its <see cref="Message"/> getter throws. Used to prove
/// that the code which turns plugin exceptions into reason texts cannot be broken by the exception it describes. Immutable.
/// </summary>
internal sealed class HostileException : Exception
{
    /// <summary>Always throws <see cref="InvalidOperationException"/>.</summary>
    public override string Message => throw new InvalidOperationException("hostile message getter");
}
