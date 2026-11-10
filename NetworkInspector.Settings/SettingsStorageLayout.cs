// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// The names an application chooses for its settings folder: the application folder name under the user's application data,
/// the folders next to the profiles that are no profiles, and the two bookkeeping files a settings folder holds (the
/// cross-process lock and the manifest of the group files the manager owns). Immutable and thread-safe.
/// </summary>
/// <remarks>
/// Every member that persists, lists profiles, or resolves a path takes a layout, and a manager cannot persist without one,
/// because the file names are part of the format on disk: a manager that wrote another manifest name than an earlier run would
/// not find the files that run owned, and would leave them behind forever. Create one layout per application, keep it for the
/// life of the process, and never change its names between releases.
/// <para>Equality compares every name; the reserved list compares in order.</para>
/// </remarks>
/// <seealso cref="SettingsManager"/>
/// <seealso cref="SettingsManagerFactory"/>
/// <seealso cref="SettingsProfiles"/>
/// <seealso cref="ProfileNames"/>
public sealed record SettingsStorageLayout
{
    #region Default file names

    /// <summary>The folder every layout reserves: the settings folder of the application itself, next to the profile folders.</summary>
    public const string ApplicationFolderName = "application";

    /// <summary>The lock file name used when a layout does not choose one.</summary>
    public const string DefaultLockFileName = ".settings.lock";

    /// <summary>The ownership manifest file name used when a layout does not choose one.</summary>
    public const string DefaultOwnedManifestFileName = ".settings-owned";

    #endregion

    #region Construction

    /// <summary>Creates a layout from validated names.</summary>
    /// <param name="applicationName">The validated application folder name.</param>
    /// <param name="reservedFolderNames">The validated reserved names, including <see cref="ApplicationFolderName"/>.</param>
    /// <param name="lockFileName">The validated lock file name.</param>
    /// <param name="ownedManifestFileName">The validated manifest file name.</param>
    private SettingsStorageLayout(string applicationName, IReadOnlyList<string> reservedFolderNames, string lockFileName, string ownedManifestFileName)
    {
        ApplicationName = applicationName;
        ReservedFolderNames = reservedFolderNames;
        LockFileName = lockFileName;
        OwnedManifestFileName = ownedManifestFileName;
    }

    /// <summary>Creates a layout after checking every name.</summary>
    /// <param name="applicationName">
    /// The folder name of the application under the user's application data folder. A plain file name of at most
    /// <see cref="StorageNames.MaxDirectLength"/> characters: no separator, no <c>..</c>, no control character, no device name.
    /// </param>
    /// <param name="reservedFolderNames">
    /// Folder names under the settings base that are not profiles, compared ignoring case. Each is a name a profile could have
    /// (<see cref="ProfileNames.TryValidateSyntax"/>) and appears once; <see cref="ApplicationFolderName"/> is added when missing.
    /// </param>
    /// <param name="lockFileName">
    /// File name of the cross-process lock inside a settings folder: starts with a dot, a plain name, does not end in
    /// <c>.json</c>, and differs from <paramref name="ownedManifestFileName"/>.
    /// </param>
    /// <param name="ownedManifestFileName">File name of the manifest of owned group files; the same rules as the lock file name.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reservedFolderNames"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A name breaks its rule; the message names the argument and the rule. This is a defect of the application's setup, found at
    /// its first run, not an expected failure.
    /// </exception>
    public static SettingsStorageLayout Create(
        string applicationName,
        IReadOnlyList<string> reservedFolderNames,
        string lockFileName = DefaultLockFileName,
        string ownedManifestFileName = DefaultOwnedManifestFileName)
    {
        // Security: every name becomes part of a path, so a name with a separator or ".." could leave the settings folder
        // (tampering) and a lock or manifest file named "*.json" would be read as a group file (denial of service for the
        // load). Mitigation: names must be plain file names and are refused otherwise. Rejected: sanitizing a name, which would
        // hide a bug in the application and write files under a name nobody chose. Residual risk: the application is trusted
        // code and may choose any plain name.
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        ArgumentNullException.ThrowIfNull(reservedFolderNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedManifestFileName);

        _CheckPlainName(applicationName, nameof(applicationName));
        string[] reserved = _CheckReserved(reservedFolderNames, nameof(reservedFolderNames));
        _CheckBookkeepingName(lockFileName, nameof(lockFileName));
        _CheckBookkeepingName(ownedManifestFileName, nameof(ownedManifestFileName));
        if (string.Equals(lockFileName, ownedManifestFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The lock file and the ownership manifest need two different names.", nameof(ownedManifestFileName));
        }

        return new SettingsStorageLayout(applicationName, Array.AsReadOnly(reserved), lockFileName, ownedManifestFileName);
    }

    #endregion

    #region Layout data

    /// <summary>The folder name of the application under the user's application data folder.</summary>
    public string ApplicationName { get; }

    /// <summary>File name of the cross-process lock inside a settings folder.</summary>
    public string LockFileName { get; }

    /// <summary>File name of the manifest of the group files a settings folder owns.</summary>
    public string OwnedManifestFileName { get; }

    /// <summary>
    /// Folder names under the settings base that are not profiles (compared ignoring case); always contains
    /// <see cref="ApplicationFolderName"/>.
    /// </summary>
    public IReadOnlyList<string> ReservedFolderNames { get; }

    /// <inheritdoc/>
    public bool Equals(SettingsStorageLayout? other) =>
        other is not null
        && string.Equals(ApplicationName, other.ApplicationName, StringComparison.Ordinal)
        && string.Equals(LockFileName, other.LockFileName, StringComparison.Ordinal)
        && string.Equals(OwnedManifestFileName, other.OwnedManifestFileName, StringComparison.Ordinal)
        && ReservedFolderNames.SequenceEqual(other.ReservedFolderNames, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ApplicationName, LockFileName, OwnedManifestFileName, ReservedFolderNames.Count);

    #endregion

    #region Name checks

    /// <summary>The longest name this layout accepts: the length that leaves room for suffixes of temporary files.</summary>
    private const int _MaxNameLength = StorageNames.MaxDirectLength;

    /// <summary>Characters no name may hold on any supported operating system, besides control characters.</summary>
    private static readonly SearchValues<char> _ForbiddenCharacters = SearchValues.Create("/\\:*?\"<>|");

    /// <summary>Checks that a name is a plain file name that is safe on every supported operating system.</summary>
    /// <param name="name">The name.</param>
    /// <param name="argument">The argument name for the message.</param>
    /// <exception cref="ArgumentException">
    /// The name is too long, holds a separator, a forbidden or control character, or <c>..</c>, or is a device name.
    /// </exception>
    private static void _CheckPlainName(string name, string argument)
    {
        if (name.Length > _MaxNameLength)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The name is longer than {_MaxNameLength} characters."), argument);
        }

        if (name.AsSpan().ContainsAny(_ForbiddenCharacters) || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("The name is a plain file name: no path separator, no ':', no wildcard, and no '..'.", argument);
        }

        foreach (char character in name)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("The name holds a control character.", argument);
            }
        }

        // A name that ends in a space or a dot names the same file as the shorter name on Windows, so two layouts could share a file.
        if (name[^1] is ' ' or '.' || StorageNames.IsDeviceName(name))
        {
            throw new ArgumentException("The name must not end in a space or a dot and must not be a device name of the operating system.", argument);
        }
    }

    /// <summary>Checks the name of the lock file or the manifest.</summary>
    /// <param name="name">The name.</param>
    /// <param name="argument">The argument name for the message.</param>
    /// <exception cref="ArgumentException">The name does not start with a dot, ends in <c>.json</c>, or is not a plain name.</exception>
    private static void _CheckBookkeepingName(string name, string argument)
    {
        if (name[0] != '.')
        {
            throw new ArgumentException("The name starts with a dot, so the file stays out of the way of group files and listings.", argument);
        }

        // The load reads every "*.json" of the folder as a group file; a bookkeeping file of that name would be parsed as one.
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The name must not end in .json: the load would read the file as a settings group.", argument);
        }

        _CheckPlainName(name, argument);
    }

    /// <summary>Checks the reserved folder names and adds <see cref="ApplicationFolderName"/> when it is missing.</summary>
    /// <param name="names">The names the application gave.</param>
    /// <param name="argument">The argument name for the message.</param>
    /// <returns>A new array that starts with <see cref="ApplicationFolderName"/> followed by the given names in order.</returns>
    /// <exception cref="ArgumentException">A name is null, cannot be a profile name, or appears twice ignoring case.</exception>
    private static string[] _CheckReserved(IReadOnlyList<string> names, string argument)
    {
        List<string> result = [ApplicationFolderName];
        bool applicationGiven = false;
        for (int index = 0; index < names.Count; index++)
        {
            string? name = names[index];
            if (name is null)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The reserved name at index {index} is null."), argument);
            }

            if (!ProfileNames.TryValidateSyntax(name, out string? rule))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"The reserved name '{name}' cannot be a folder under the settings base: {rule}."),
                    argument);
            }

            // "application" is reserved by every layout: the given name is accepted and is not added a second time.
            if (string.Equals(name, ApplicationFolderName, StringComparison.OrdinalIgnoreCase))
            {
                if (applicationGiven)
                {
                    throw new ArgumentException(
                        string.Create(CultureInfo.InvariantCulture, $"The reserved name '{name}' appears twice (names compare ignoring case)."),
                        argument);
                }

                applicationGiven = true;
                continue;
            }

            if (result.Exists(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"The reserved name '{name}' appears twice (names compare ignoring case)."),
                    argument);
            }

            result.Add(name);
        }

        return [.. result];
    }

    #endregion
}
