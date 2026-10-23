// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// A mistake of the user that is found late in a run, for example an option text that only the factory of an exporter can judge
/// when the first output opens. The command line maps it to <see cref="ExitCode.ArgumentError"/> with one error line, like an
/// argument problem found while parsing.
/// </summary>
internal sealed class CliUsageException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The one-line message shown after <c>Error:</c>.</param>
    internal CliUsageException(string message)
        : base(message)
    {
    }
}
