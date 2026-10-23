// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>How a <see cref="LifePlugin"/> behaves in <c>StartAsync</c>.</summary>
internal enum StartBehavior
{
    /// <summary>Logs <c>started</c> and returns.</summary>
    Normal,

    /// <summary>Throws <c>start boom</c>.</summary>
    Throw,

    /// <summary>Waits until its token is cancelled.</summary>
    WaitForToken,

    /// <summary>Never completes and ignores its token.</summary>
    NeverComplete,

    /// <summary>Blocks its thread for 30 s before it returns a task (a plugin that waits synchronously).</summary>
    BlockThread,

    /// <summary>Registers a callback on its token that throws, then waits for the token.</summary>
    ThrowingCallbackThenWait,
}

/// <summary>How a <see cref="LifePlugin"/> behaves in <c>StopAsync</c>.</summary>
internal enum StopBehavior
{
    /// <summary>Returns.</summary>
    Normal,

    /// <summary>Throws <c>stop boom</c>.</summary>
    Throw,

    /// <summary>Waits until its token is cancelled.</summary>
    WaitForToken,

    /// <summary>Blocks its thread for 30 s before it returns (a plugin that waits synchronously).</summary>
    BlockThread,
}

/// <summary>
/// A built-in test plugin with configurable lifecycle behavior. It writes <c>name.Configure</c>, <c>name.Start</c>, and
/// <c>name.Stop</c> into a shared ordered list (guarded by a lock on the list), registers a source kind when asked, and records
/// whether its stopping token was already cancelled when <c>StopAsync</c> ran. Thread safety: lifecycle calls are sequential.
/// </summary>
internal sealed class LifePlugin : IPlugin
{
    #region Fields

    private readonly string _Name;

    private readonly List<string> _Calls;

    private readonly StartBehavior _Start;

    private readonly StopBehavior _Stop;

    private readonly string? _SourceKind;

    private IPluginContext? _Context;

    #endregion

    #region Lifecycle

    /// <summary>Creates the plugin.</summary>
    /// <param name="name">Plugin name.</param>
    /// <param name="calls">Shared call log.</param>
    /// <param name="start">Behavior of <c>StartAsync</c>.</param>
    /// <param name="stop">Behavior of <c>StopAsync</c>.</param>
    /// <param name="sourceKind">Source kind to register, or <see langword="null"/>.</param>
    internal LifePlugin(string name, List<string> calls, StartBehavior start = StartBehavior.Normal, StopBehavior stop = StopBehavior.Normal,
        string? sourceKind = null)
    {
        _Name = name;
        _Calls = calls;
        _Start = start;
        _Stop = stop;
        _SourceKind = sourceKind;
    }

    #endregion

    #region Public API

    /// <summary>Whether <c>Stopping</c> was already cancelled when <c>StopAsync</c> ran; <see langword="null"/> before it ran.</summary>
    internal bool? StoppingWasCancelledAtStop { get; private set; }

    /// <inheritdoc/>
    public PluginInfo Info => new(_Name, "1.0.0", "MIT", "Lifecycle test plugin.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
        _Add(".Configure");
        if (_SourceKind is not null)
        {
            builder.AddSource(new SourceRegistration(_SourceKind, _Name, "Source of " + _Name, [], false, static _ => { }, _Refuse));
        }
    }

    /// <inheritdoc/>
    public async ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _Context = context;
        _Add(".Start");
        switch (_Start)
        {
            case StartBehavior.Throw:
                throw new InvalidOperationException("start boom");
            case StartBehavior.WaitForToken:
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                break;
            case StartBehavior.BlockThread:
                Thread.Sleep(TimeSpan.FromSeconds(30));
                break;
            case StartBehavior.ThrowingCallbackThenWait:
                cancellationToken.Register(static () => throw new InvalidOperationException("callback boom"));
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                break;
            case StartBehavior.NeverComplete:
                await new TaskCompletionSource().Task.ConfigureAwait(false);
                break;
            default:
                context.Log.Info("started");
                break;
        }
    }

    /// <inheritdoc/>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _Add(".Stop");
        StoppingWasCancelledAtStop = _Context?.Stopping.IsCancellationRequested;
        switch (_Stop)
        {
            case StopBehavior.Throw:
                throw new InvalidOperationException("stop boom");
            case StopBehavior.BlockThread:
                Thread.Sleep(TimeSpan.FromSeconds(30));
                break;
            case StopBehavior.WaitForToken:
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                break;
            default:
                _Context?.Log.Info("stopped");
                break;
        }
    }

    #endregion

    #region Private helpers

    private void _Add(string suffix)
    {
        lock (_Calls)
        {
            _Calls.Add(_Name + suffix);
        }
    }

    private static bool _Refuse(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source, [NotNullWhen(false)] out PluginError? error)
    {
        source = null;
        error = new PluginError(PluginErrorCode.Failed, "The lifecycle test source creates nothing.");
        return false;
    }

    #endregion
}
