// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Tshark;

/// <summary>
/// Centralizes <see cref="ProcessStartInfo"/> construction and execution for all tshark
/// invocations across the Network Inspector test projects.
/// </summary>
/// <remarks>
/// <para>The personal Wireshark configuration directory is overridden via
/// <c>WIRESHARK_CONFIG_DIR</c> to a non-existent folder so tshark falls back to builtin
/// defaults and skips user Lua post-dissectors. Such plugins (e.g. <c>postdissector.lua</c>)
/// can print extra lines to stdout per packet, which would derail the structured tshark
/// output parsers used by Network Inspector tests.</para>
/// <para>Thread-safety: the type is fully static and stateless; safe for concurrent use.</para>
/// </remarks>
internal static class TsharkProcess
{
    /// <summary>
    /// Path used as <c>WIRESHARK_CONFIG_DIR</c>. Intentionally non-existent so tshark
    /// loads no user Lua plugins.
    /// </summary>
    private static readonly string _NoLuaConfigDir =
        Path.Combine(Path.GetTempPath(), "ni_tshark_no_user_plugins");

    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> targeting <c>tshark</c> with stdout/stderr
    /// redirected, no shell, no window, UTF-8 encoding, and user Lua plugins suppressed.
    /// </summary>
    /// <param name="arguments">Plain tshark CLI arguments (without any profile-related flags).</param>
    /// <param name="profileDir">
    /// Optional path to a per-test profile directory. When supplied, the parent directory
    /// is exposed as <c>WIRESHARK_CONFIG_DIR</c> (so tshark resolves the profile under
    /// <c>profiles/&lt;name&gt;/</c>) and <c>-C &lt;name&gt;</c> is prepended to
    /// <paramref name="arguments"/>. The profile directory itself contains no
    /// <c>init.lua</c>, so user Lua plugins remain suppressed by virtue of the empty
    /// personal-config dir.
    /// </param>
    internal static ProcessStartInfo BuildStartInfo(string arguments, string? profileDir = null)
    {
        string finalArguments;
        string configDir;

        if (profileDir is null)
        {
            finalArguments = arguments;
            configDir = _NoLuaConfigDir;
        }
        else
        {
            // tshark resolves a profile through `<personal>/profiles/<name>/`.
            // Our generator builds `<base>/profiles/<runId>/`, so the parent of that
            // sub-folder is the personal-config dir we expose to tshark.
            string profilesParent = Path.GetDirectoryName(profileDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                ?? throw new ArgumentException("profileDir must have a parent directory.", nameof(profileDir));
            string personalDir = Path.GetDirectoryName(profilesParent)
                ?? throw new ArgumentException("profileDir must live under a 'profiles' folder.", nameof(profileDir));
            string profileName = Path.GetFileName(profileDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            // Preferences embedded in freshly generated profiles sometimes load too late or
            // differ by platform encoding; `-o` is applied before `-r`/dissection so Signal-PDU
            // raw subtree fields (-T fields) match Network Inspector asserts.
            const string hideRawPref = "-o \"signal_pdu.payload_dissector_hide_raw_values:false\"";

            // -C must come BEFORE -r/-T/-e so tshark applies the profile while parsing
            // the rest of the args.
            finalArguments = $"{hideRawPref} -C \"{profileName}\" {arguments}";
            configDir = personalDir;
        }

        ProcessStartInfo info = new("tshark", finalArguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment["WIRESHARK_CONFIG_DIR"] = configDir;
        return info;
    }

    /// <summary>
    /// Runs tshark with the given <paramref name="arguments"/>, waits up to
    /// <paramref name="timeoutMs"/> milliseconds, and returns the exit code together with
    /// stdout/stderr. The process is killed if it exceeds the timeout.
    /// </summary>
    /// <remarks>
    /// stdout and stderr are drained concurrently via <see cref="StreamReader.ReadToEndAsync()"/>
    /// to prevent the OS pipe-buffer deadlock that would occur if stdout were read to completion
    /// before stderr is drained: any child process that writes more data to one pipe than the OS
    /// pipe buffer can hold will block until the parent drains that pipe, causing an indefinite
    /// hang when the parent is itself blocking on the other pipe.
    /// </remarks>
    /// <param name="arguments">Plain tshark CLI arguments (without any profile-related flags).</param>
    /// <param name="timeoutMs">Wall-clock timeout in milliseconds; on expiry the process is killed.</param>
    /// <param name="profileDir">Optional profile directory; see <see cref="BuildStartInfo"/>.</param>
    /// <returns>A tuple of (exitCode, stdout, stderr, timedOut).</returns>
    internal static (int ExitCode, string StdOut, string StdErr, bool TimedOut) Run(
        string arguments,
        int timeoutMs,
        string? profileDir = null)
    {
        using Process process = new();
        process.StartInfo = BuildStartInfo(arguments, profileDir);
        process.Start();
        // stdout and stderr MUST be drained concurrently. Reading them sequentially
        // causes a classic OS pipe deadlock: if the child writes enough data to fill
        // the stderr pipe buffer while the parent is blocked in stdout.ReadToEnd(),
        // neither side can make progress.  Using ReadToEndAsync starts both drains
        // immediately on the thread-pool so neither buffer can fill up.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort — process may already have exited between WaitForExit and Kill.
            }
            // Drain whatever was written before the kill so the tasks complete.
            string partialStdout = stdoutTask.GetAwaiter().GetResult();
            string partialStderr = stderrTask.GetAwaiter().GetResult();
            return (-1, partialStdout, partialStderr, true);
        }
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult(), false);
    }
}
