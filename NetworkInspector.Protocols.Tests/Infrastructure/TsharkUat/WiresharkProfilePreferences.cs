// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.TsharkUat;

/// <summary>
/// Appends deterministic preference overrides inside a freshly created per-test Wireshark profile.
/// </summary>
internal static class WiresharkProfilePreferences
{
    internal static void MergeIntoProfilePreferences(string profileDirectory)
    {
        string path = Path.Combine(profileDirectory, "preferences");

        /*
         Hide raw hides the subtree that -T fields would otherwise fail to populate for symmetric asserts.
        */
        ReadOnlySpan<string> appendedLines =
        [
            "# Network Inspector tests — deterministic Signal Message subtree visibility.",
            "# signal_pdu.payload_dissector_hide_raw_values",
            "signal_pdu.payload_dissector_hide_raw_values: FALSE",
        ];

        Directory.CreateDirectory(profileDirectory);

        Encoding utf = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (File.Exists(path))
        {
            using StreamWriter w = new(path, append: true, utf);
            w.WriteLine();
            foreach (string line in appendedLines)
            {
                w.WriteLine(line);
            }

            return;
        }

        using (StreamWriter w = new(path, append: false, utf))
        {
            foreach (string line in appendedLines)
            {
                w.WriteLine(line);
            }
        }
    }
}
