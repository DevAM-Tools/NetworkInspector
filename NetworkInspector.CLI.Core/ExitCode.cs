// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// Well-known process exit codes returned by the CLI.
/// </summary>
internal enum ExitCode
{
    /// <summary>Command completed successfully.</summary>
    Success = 0,

    /// <summary>Bad arguments, missing options, or validation failure.</summary>
    ArgumentError = 1,

    /// <summary>Failed to open one or more source files.</summary>
    SourceOpenError = 2,

    /// <summary>Runtime failure during processing (IO, serialisation, etc.).</summary>
    RuntimeError = 3,
}
