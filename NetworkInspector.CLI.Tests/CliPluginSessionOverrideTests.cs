// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>How the <c>--set</c> overrides reach the settings that a session and the stack of a command read.</summary>
internal sealed class CliPluginSessionOverrideTests
{
    [Test]
    public async Task AnOverrideOfAProfileSettingReachesTheStackThatTheSessionBuilds()
    {
        using CliSandbox sandbox = new();
        CliSettingsOptions options = CliSettingsOptions.Defaults with
        {
            SettingsPath = sandbox.Base,
            Overrides = [new KeyValuePair<string, string>("tcp.max_tracked_streams", "777")],
        };
        CliIo io = new(TextWriter.Null, TextWriter.Null);

        (CliPluginSession? opened, int exit) = await CliPluginSession.OpenAsync(options, io, CancellationToken.None);
        CliPluginSession session = opened ?? throw new InvalidOperationException("The session did not open.");

        await Assert.That(exit).IsEqualTo(0);
        await using (session.ConfigureAwait(false))
        {
            FrameInterfaceRegistry frames = new();
            bool built = session.TryBuildStack(frames, out Stack? stack, out int stackExit);
            await Assert.That(built).IsTrue();
            await Assert.That(stackExit).IsEqualTo(0);
            using (stack)
            {
                await Assert.That(stack!.Settings.GetU64Setting("tcp.max_tracked_streams")).IsEqualTo((ulong?)777UL);
            }

            await Assert.That(session.ProfileSettings.GetU64Setting("tcp.max_tracked_streams")).IsEqualTo((ulong?)777UL);
        }

        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "default", "tcp.json"))).IsFalse();
    }

    [Test]
    public async Task TheProfileManagersLoadWithTheLoaderLimitsInEffectOfTheRun()
    {
        using CliSandbox sandbox = new();
        CliSettingsOptions options = CliSettingsOptions.Defaults with
        {
            SettingsPath = sandbox.Base,
            Overrides = [new KeyValuePair<string, string>("settings.maxJsonDepth", "12")],
        };
        CliIo io = new(TextWriter.Null, TextWriter.Null);

        (CliPluginSession? opened, int exit) = await CliPluginSession.OpenAsync(options, io, CancellationToken.None);
        CliPluginSession session = opened ?? throw new InvalidOperationException("The session did not open.");

        await Assert.That(exit).IsEqualTo(0);
        await using (session.ConfigureAwait(false))
        {
            await Assert.That(session.LoaderLimits.MaxJsonDepth).IsEqualTo(LimitValue.Of(12));
            await Assert.That(session.ProfileSettings.Limits.MaxJsonDepth).IsEqualTo(LimitValue.Of(12));
            await Assert.That(session.ApplicationSettings.GetLimit("settings.maxJsonDepth")).IsEqualTo((LimitValue?)LimitValue.Of(12));
        }
    }
}
