// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// A mistake of the user that is found late in a run, for example an option text that only the factory of an exporter can judge
/// when the first output opens. The command line maps it to <see cref="ExitCode.ArgumentError"/> with one error line, like an
/// argument problem found while parsing. The run loop of a command turns every other exception, an <see cref="ArgumentException"/>
/// included, into exit code 3, so a mistake of the user that surfaces inside the loop needs its own type to reach exit code 1
/// (the commands catch it before their general handler).
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
