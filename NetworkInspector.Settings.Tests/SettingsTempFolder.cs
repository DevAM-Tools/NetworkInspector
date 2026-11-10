// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>
/// A unique temporary folder per test for the settings persistence tests, deleted on dispose. Tests run in parallel, so no
/// test shares a folder.
/// </summary>
internal sealed class SettingsTempFolder : IDisposable
{
    /// <summary>Creates the folder.</summary>
    internal SettingsTempFolder()
    {
        Path = Directory.CreateTempSubdirectory("settings-test-").FullName;
    }

    /// <summary>Full path of the folder.</summary>
    internal string Path { get; }

    /// <summary>Combines the folder path with a file or folder name.</summary>
    /// <param name="name">File or folder name.</param>
    /// <returns>The full path inside the folder.</returns>
    internal string Combine(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Writes a text file into the folder.</summary>
    /// <param name="name">File name.</param>
    /// <param name="content">The content.</param>
    internal void Write(string name, string content) => File.WriteAllText(Combine(name), content);

    /// <summary>Reads a file of the folder as JSON.</summary>
    /// <param name="name">File name.</param>
    /// <returns>The parsed root object.</returns>
    internal JsonObject ReadJson(string name)
    {
        JsonObject? root = JsonNode.Parse(File.ReadAllText(Combine(name))) as JsonObject;
        if (root is null)
        {
            throw new InvalidOperationException(name + " is not a JSON object.");
        }

        return root;
    }

    /// <summary>Deletes the folder with everything in it; failures are ignored because the operating system cleans the temp area.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A scanner or an antivirus may hold a file briefly; the leftover folder is harmless.
        }
    }
}
