// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// A session-wide fixture that compiles stand-ins for the contracts of the higher tiers — <c>NetworkInspector.Host.Abstractions</c>
/// with <c>IHostPlugin</c> and <c>NetworkInspector.App.Plugins</c> with <c>IAppPlugin</c>, carrying the real assembly and type
/// names — plus fake plugins and logging plugins that implement them. The pre-scan recognizes tiers by those names, so the
/// loader tier rules can be tested before the real higher contracts exist. <see cref="LoadIntoDefault"/> makes the two
/// contracts available in the default load context, as a real host or app has them. Thread-safe.
/// </summary>
internal sealed class DummyContracts : IAsyncInitializer, IAsyncDisposable
{
    #region Constants

    private const string _ContractSource = """
        namespace NetworkInspector.Host
        {
            public interface IHostPlugin : NetworkInspector.Plugins.IPlugin
            {
                void ConfigureHost(NetworkInspector.Plugins.IPluginBuilder builder);
            }
        }
        """;

    private const string _AppContractSource = """
        namespace NetworkInspector.App
        {
            public interface IAppPlugin : NetworkInspector.Host.IHostPlugin
            {
                void ConfigureApp(NetworkInspector.Plugins.IPluginBuilder builder);
            }
        }
        """;

    private const string _FakeHostSource = """
        using NetworkInspector.Plugins;
        public sealed class FakeHostPlugin : NetworkInspector.Host.IHostPlugin
        {
            public PluginInfo Info { get; } = new("Fake.Host", "1.0.0", "MIT", "Fake host-tier plugin.", []);
            public void Configure(IPluginBuilder builder) { }
            public void ConfigureHost(IPluginBuilder builder) { }
        }
        """;

    private const string _FakeAppSource = """
        using NetworkInspector.Plugins;
        public sealed class FakeAppPlugin : NetworkInspector.App.IAppPlugin
        {
            public PluginInfo Info { get; } = new("Fake.App", "1.0.0", "MIT", "Fake app-tier plugin.", []);
            public void Configure(IPluginBuilder builder) { }
            public void ConfigureHost(IPluginBuilder builder) { }
            public void ConfigureApp(IPluginBuilder builder) { }
        }
        """;

    // Logging plugins A, B, D (host tier) and C (core tier). Each writes "<name>.<method>" into the list the test assigns to
    // Calls; B can throw in its host method, and every plugin registers a source of its own so that "not published" is visible.
    private const string _LoggingSource = """
        using System;
        using System.Collections.Generic;
        using NetworkInspector.Plugins;
        public abstract class LoggingPluginBase : IPlugin
        {
            private readonly string _Name;
            protected LoggingPluginBase(string name) { _Name = name; }
            public List<string>? Calls { get; set; }
            public bool ThrowInHost { get; set; }
            public PluginInfo Info => new(_Name, "1.0.0", "MIT", "Logging test plugin.", []);
            public void Configure(IPluginBuilder builder)
            {
                Calls?.Add(_Name + ".Configure");
                builder.AddSource(new SourceRegistration("Source" + _Name, _Name, "Source of " + _Name, [], false, static _ => { }, Refuse));
            }
            protected void HostConfigured()
            {
                Calls?.Add(_Name + ".ConfigureHost");
                if (ThrowInHost) { throw new InvalidOperationException("host boom"); }
            }
            private static bool Refuse(SourceRequest request, out NetworkInspector.Core.Interfaces.IFrameSource? source, out PluginError? error)
            {
                source = null;
                error = new PluginError(PluginErrorCode.Failed, "not created");
                return false;
            }
        }
        public sealed class PluginA : LoggingPluginBase, NetworkInspector.Host.IHostPlugin
        {
            public PluginA() : base("A") { }
            public void ConfigureHost(IPluginBuilder builder) { HostConfigured(); }
        }
        public sealed class PluginB : LoggingPluginBase, NetworkInspector.Host.IHostPlugin
        {
            public PluginB() : base("B") { }
            public void ConfigureHost(IPluginBuilder builder) { HostConfigured(); }
        }
        public sealed class PluginC : LoggingPluginBase
        {
            public PluginC() : base("C") { }
        }
        public sealed class PluginD : LoggingPluginBase, NetworkInspector.Host.IHostPlugin
        {
            public PluginD() : base("D") { }
            public void ConfigureHost(IPluginBuilder builder) { HostConfigured(); }
        }
        """;

    #endregion

    #region Fields

    private readonly Lock _LoadLock = new();

    private string _Folder = string.Empty;

    private Assembly? _HostContract;

    private Assembly? _AppContract;

    private Assembly? _Logging;

    #endregion

    #region Lifecycle

    /// <summary>Compiles the contracts and the plugins into a session folder; runs once per test session.</summary>
    /// <returns>A completed task.</returns>
    public Task InitializeAsync()
    {
        _Folder = Directory.CreateTempSubdirectory("ni-dummy-contracts-").FullName;
        string host = TestAssemblyCompiler.Compile(Path.Combine(_Folder, "contracts", "NetworkInspector.Host.Abstractions.dll"),
            "NetworkInspector.Host.Abstractions", _ContractSource);
        string app = TestAssemblyCompiler.Compile(Path.Combine(_Folder, "contracts", "NetworkInspector.App.Plugins.dll"),
            "NetworkInspector.App.Plugins", _AppContractSource, [host]);
        FakeHostPath = TestAssemblyCompiler.Compile(Path.Combine(_Folder, "fakes", "Fake.Host.dll"), "Fake.Host", _FakeHostSource, [host]);
        FakeAppPath = TestAssemblyCompiler.Compile(Path.Combine(_Folder, "fakes", "Fake.App.dll"), "Fake.App", _FakeAppSource, [host, app]);
        HostContractPath = host;
        AppContractPath = app;
        LoggingPath = TestAssemblyCompiler.Compile(Path.Combine(_Folder, "logging", "LoggingPlugins.dll"), "LoggingPlugins", _LoggingSource, [host]);
        return Task.CompletedTask;
    }

    /// <summary>Deletes the session folder.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Loaded assemblies keep their files open on Windows; the operating system cleans the temp area.
        }

        return ValueTask.CompletedTask;
    }

    #endregion

    #region Public API

    /// <summary>Path of the compiled host-tier contract assembly.</summary>
    internal string HostContractPath { get; private set; } = string.Empty;

    /// <summary>Path of the compiled app-tier contract assembly.</summary>
    internal string AppContractPath { get; private set; } = string.Empty;

    /// <summary>Path of the assembly with the fake host-tier plugin.</summary>
    internal string FakeHostPath { get; private set; } = string.Empty;

    /// <summary>Path of the assembly with the fake app-tier plugin.</summary>
    internal string FakeAppPath { get; private set; } = string.Empty;

    /// <summary>Path of the assembly with the logging plugins.</summary>
    internal string LoggingPath { get; private set; } = string.Empty;

    /// <summary>Loads the two contracts and the logging plugins into the default load context once.</summary>
    internal void LoadIntoDefault()
    {
        lock (_LoadLock)
        {
            _HostContract ??= AssemblyLoadContext.Default.LoadFromAssemblyPath(HostContractPath);
            _AppContract ??= AssemblyLoadContext.Default.LoadFromAssemblyPath(AppContractPath);
            _Logging ??= AssemblyLoadContext.Default.LoadFromAssemblyPath(LoggingPath);
        }
    }

    /// <summary>
    /// Creates the logging plugins A, B, C, and D as built-in instances; each writes <c>name.method</c> into
    /// <paramref name="calls"/>. B throws <c>host boom</c> in its host method when <paramref name="bThrows"/> is set.
    /// </summary>
    /// <param name="calls">The shared, ordered call log (guarded by a lock on the list itself).</param>
    /// <param name="bThrows">Whether plugin B throws in <c>ConfigureHost</c>.</param>
    /// <returns>The four plugin instances in the order A, B, C, D.</returns>
    internal IReadOnlyList<IPlugin> CreateLoggingPlugins(List<string> calls, bool bThrows = false)
    {
        LoadIntoDefault();
        Assembly logging = _Logging ?? throw new InvalidOperationException("The logging plugins are not loaded.");
        List<IPlugin> plugins = [];
        foreach (string typeName in (string[])["PluginA", "PluginB", "PluginC", "PluginD"])
        {
            Type type = logging.GetType(typeName) ?? throw new InvalidOperationException("Missing test type " + typeName);
            object instance = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Cannot create " + typeName);
            (type.GetProperty("Calls") ?? throw new InvalidOperationException("Missing property Calls")).SetValue(instance, calls);
            (type.GetProperty("ThrowInHost") ?? throw new InvalidOperationException("Missing property ThrowInHost")).SetValue(instance, bThrows && typeName == "PluginB");
            plugins.Add((IPlugin)instance);
        }

        return plugins;
    }

    /// <summary>The host-tier step: calls <c>ConfigureHost</c> through the dummy interface and logs when <paramref name="log"/> is given.</summary>
    /// <param name="log">Optional log of <c>name.ConfigureHost</c> entries.</param>
    /// <returns>The tier step.</returns>
    internal PluginTierStep HostStep(List<string>? log = null) =>
        new(PluginTier.Host, "ConfigureHost", (plugin, builder) => _Invoke("NetworkInspector.Host.IHostPlugin", "ConfigureHost", plugin, builder, log));

    /// <summary>The app-tier step: calls <c>ConfigureApp</c> through the dummy interface and logs when <paramref name="log"/> is given.</summary>
    /// <param name="log">Optional log of <c>name.ConfigureApp</c> entries.</param>
    /// <returns>The tier step.</returns>
    internal PluginTierStep AppStep(List<string>? log = null) =>
        new(PluginTier.App, "ConfigureApp", (plugin, builder) => _Invoke("NetworkInspector.App.IAppPlugin", "ConfigureApp", plugin, builder, log));

    /// <summary>The assembly of the host contract as loaded in the default context.</summary>
    internal Assembly HostContract
    {
        get
        {
            LoadIntoDefault();
            Assembly? contract = _HostContract;
            if (contract is null)
            {
                throw new InvalidOperationException("The host contract is not loaded.");
            }

            return contract;
        }
    }

    #endregion

    #region Private helpers

    private void _Invoke(string interfaceName, string methodName, IPlugin plugin, IPluginBuilder builder, List<string>? log)
    {
        Assembly contract = interfaceName.Contains(".App.", StringComparison.Ordinal)
            ? _AppContract ?? throw new InvalidOperationException("The app contract is not loaded.")
            : HostContract;
        Type contractType = contract.GetType(interfaceName) ?? throw new InvalidOperationException("Missing contract " + interfaceName);
        if (log is not null)
        {
            lock (log)
            {
                log.Add(plugin.Info.Name + "." + methodName);
            }
        }

        try
        {
            (contractType.GetMethod(methodName) ?? throw new InvalidOperationException("Missing method " + methodName)).Invoke(plugin, [builder]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // The step must behave like a direct call: the plugin's own exception, not the reflection wrapper.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    #endregion
}
