// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// Well-known process exit codes returned by the CLI. The numbers are part of the tool's contract with scripts and CI (they are
/// listed in the README of the tool), so a value is never renumbered and a new failure class gets a new value.
/// </summary>
internal enum ExitCode
{
    /// <summary>Command completed successfully; also the result of a run that Ctrl+C cancelled (its unfinished output is removed).</summary>
    Success = 0,

    /// <summary>
    /// Bad arguments, missing options, or validation failure: also a refused existing output, an unknown setting or profile,
    /// a filter that does not compile, and an invalid option text.
    /// </summary>
    ArgumentError = 1,

    /// <summary>Failed to open one or more source files.</summary>
    SourceOpenError = 2,

    /// <summary>
    /// Runtime failure during processing (IO, serialisation, etc.): also a packet that fails to parse, a filter that fails to
    /// evaluate, a settings folder locked by another process, and an unhandled exception.
    /// </summary>
    RuntimeError = 3,
}
