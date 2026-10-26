// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// Creates a Core source that is not started yet. Returns <see langword="false"/> with an error for every expected failure
/// (missing or unreadable file, unsupported format, invalid option combination) and never throws for them; option types and
/// bounds were already checked by Core settings. May be called concurrently, so an implementation keeps no unsynchronized
/// shared state. Expected failures are returned (not thrown) so that callers can show one precise line and map the code to
/// an exit code; a throw signals a bug in the plugin.
/// </summary>
/// <param name="request">The request: file of this source, applied option settings, session settings.</param>
/// <param name="source">The created source, not started; <see langword="null"/> on failure.</param>
/// <param name="error">Why no source was created; <see langword="null"/> on success.</param>
/// <returns><see langword="true"/> when <paramref name="source"/> is set.</returns>
public delegate bool SourceCreate(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
    [NotNullWhen(false)] out PluginError? error);
