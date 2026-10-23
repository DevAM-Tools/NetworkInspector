// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>
/// A plugin whose type initializer throws, so the type cannot be created. The initializer is a static field whose value is
/// computed by a throwing method; the instance constructor reads the field, so creating the plugin triggers the
/// initializer exactly when the loader creates the object.
/// </summary>
public sealed class StaticCtorThrowsPlugin : IPlugin
{
    private static readonly object _Boom = _Throw();

    /// <summary>Creates the plugin; reading the static field runs the throwing type initializer.</summary>
    public StaticCtorThrowsPlugin()
    {
        _ = _Boom;
    }

    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Test.StaticCtorThrows", "1.0.0", "MIT", "Type initializer throws.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
    }

    private static object _Throw() => throw new InvalidOperationException("static boom");
}
