// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>Kinds of expected failures of a source or exporter registration.</summary>
public enum PluginErrorCode : byte
{
    /// <summary>An option (or the file list) has a value the registration cannot use.</summary>
    InvalidOption,

    /// <summary>A file or folder that the request needs does not exist.</summary>
    FileNotFound,

    /// <summary>A file or folder exists but cannot be read or written (permissions, locked, I/O error).</summary>
    FileUnreadable,

    /// <summary>The file exists but is not valid for this source kind.</summary>
    UnsupportedFormat,

    /// <summary>The export target already exists and the caller did not ask to replace it.</summary>
    TargetExists,

    /// <summary>Any other expected failure; the message says what happened.</summary>
    Failed,
}
