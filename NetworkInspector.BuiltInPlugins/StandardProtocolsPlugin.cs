// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// Registers the dissectors of <c>NetworkInspector.Protocols</c> in every stack the application builds. The standard
/// protocols are a plugin like any other, so a stack without this plugin is a lean stack.
/// <para>
/// Thread safety: the stack contribution runs for every stack on any thread. The only state is the log reference, which is
/// written by <see cref="StartAsync"/> and <see cref="StopAsync"/> and read by the contribution; a single volatile
/// reference needs no lock (one word, no invariant spanning several fields).
/// </para>
/// </summary>
public sealed class StandardProtocolsPlugin : IPlugin
{
    #region Fields

    /// <summary>Log of the started plugin; <see langword="null"/> before the start and after the stop.</summary>
    private volatile IPluginLog? _Log;

    #endregion

    #region IPlugin

    /// <inheritdoc/>
    public PluginInfo Info { get; } = BuiltInPluginInfo.Create("NetworkInspector.StandardProtocols",
        "Standard protocol dissectors of NetworkInspector.Protocols (Ethernet, IP, TCP, UDP, DNS, TLS, HTTP, CAN, and more).");

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddStackContribution(_Register);
    }

    /// <inheritdoc/>
    public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _Log = context.Log;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        // A stack built after the stop must not call into a log that may be gone.
        _Log = null;
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Private Helpers

    /// <summary>Registers the standard protocols and logs their configuration warnings.</summary>
    /// <param name="stack">The Core builder of the stack under construction.</param>
    private void _Register(IStackBuilder stack)
    {
        IReadOnlyList<SettingsLoadWarning> warnings = stack.RegisterStandardProtocols();

        // The probe build at load time runs before the start, so there is no log: its warnings concern an empty in-memory
        // profile and would only confuse. Stacks of real sessions run after the start and report their warnings.
        IPluginLog? log = _Log;
        if (log is null)
        {
            return;
        }

        foreach (SettingsLoadWarning warning in warnings)
        {
            log.Warning(warning.ToString());
        }
    }

    #endregion
}
