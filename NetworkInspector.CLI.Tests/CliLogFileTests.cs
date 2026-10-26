// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>The log file of the command line: line format, rotation, plugin log lines, failures, and concurrent appends.</summary>
internal sealed class CliLogFileTests
{
    #region Helpers

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // A console that has gone away: every write fails.
    private sealed class BrokenWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("The console is closed.");
    }

    // A writer that records whether the lock of the writer itself was held while a line was written.
    private sealed class LockProbeWriter : StringWriter
    {
        public bool WroteUnderLock { get; private set; }

        public override void WriteLine(string? value)
        {
            WroteUnderLock = Monitor.IsEntered(this);
            base.WriteLine(value);
        }
    }

    private static readonly DateTimeOffset _Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CliLogFile _Open(string path, long maxBytes, int maxFiles)
    {
        if (!CliLogFile.TryOpen(path, maxBytes, maxFiles, new FixedTimeProvider(_Start), out CliLogFile? file, out string? problem))
        {
            throw new InvalidOperationException(problem);
        }

        return file;
    }

    #endregion

    #region Lines and rotation

    [Test]
    public async Task EachLineHasTimeLevelSourceAndTextOnOneLine()
    {
        using CliSandbox sandbox = new();
        string path = Path.Combine(sandbox.Root, "logs", "ni.log");
        using (CliLogFile file = _Open(path, 1_000_000, 3))
        {
            file.Append("Info", "Sample.Protocol", "text");
            file.Append("Error", "Sample.Protocol", "two\r\nlines\nhere");
        }

        string[] lines = File.ReadAllLines(path);

        await Assert.That(lines).IsEquivalentTo(
            ["2026-01-01T00:00:00.0000000Z Info Sample.Protocol: text", "2026-01-01T00:00:00.0000000Z Error Sample.Protocol: two  lines here"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task WritingPastTheLimitRotatesAndKeepsAtMostTheGivenNumberOfFiles()
    {
        using CliSandbox sandbox = new();
        string directory = Path.Combine(sandbox.Root, "logs");
        string path = Path.Combine(directory, "ni.log");
        using (CliLogFile file = _Open(path, 1024, 3))
        {
            for (int i = 0; i < 100; i++)
            {
                file.Append("Info", "Source", "entry " + i.ToString(CultureInfo.InvariantCulture) + " " + new string('x', 40));
            }
        }

        string[] files = [.. Directory.EnumerateFiles(directory).Select(static name => Path.GetFileName(name)).Order(StringComparer.Ordinal)];

        await Assert.That(files).IsEquivalentTo(["ni.1.log", "ni.2.log", "ni.log"], CollectionOrdering.Matching);
        foreach (string name in files)
        {
            await Assert.That(new FileInfo(Path.Combine(directory, name)).Length).IsLessThanOrEqualTo(1024L);
        }

        // The newest entry is in the current file, an older one in a rotated file.
        await Assert.That(File.ReadAllText(path)).Contains("entry 99 ");
        await Assert.That(File.ReadAllText(Path.Combine(directory, "ni.1.log"))).Contains("entry ");
    }

    [Test]
    public async Task ThePluginLogAddsTheExceptionToTheLineButNoStackTrace()
    {
        using CliSandbox sandbox = new();
        string path = Path.Combine(sandbox.Root, "logs", "ni.log");
        StringWriter error = new();
        using (CliLogFile file = _Open(path, 1_000_000, 3))
        {
            CliPluginLog log = new("Sample.Protocol", error, file);
            log.Error("it broke", new InvalidOperationException("boom"));
            log.Error("StartAsync failed: InvalidOperationException: boom", new InvalidOperationException("boom"));
            log.Warning("careful");
        }

        string[] lines = File.ReadAllLines(path);

        await Assert.That(error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)).IsEquivalentTo(
            ["Error Sample.Protocol: it broke (InvalidOperationException: boom)",
                "Error Sample.Protocol: StartAsync failed: InvalidOperationException: boom",
                "Warning Sample.Protocol: careful"], CollectionOrdering.Matching);
        await Assert.That(lines[0]).EndsWith("Error Sample.Protocol: it broke (InvalidOperationException: boom)");
        await Assert.That(lines.Any(static line => line.Contains("   at ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task AnErrorWithoutTextIsRefusedWithOrWithoutAnException()
    {
        CliPluginLog log = new("Sample.Protocol", TextWriter.Null, null);

        // The exception is looked at only after the text was checked, so a missing text is the bug of the plugin in both cases.
        await Assert.That(() => log.Error(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => log.Error(null!, new InvalidOperationException("boom"))).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task AProblemOfTheLogFileIsPrintedLikeTheWarningForALogFileThatCannotBeOpened()
    {
        LockProbeWriter error = new();

        CliPluginSession.WriteLogProblem(new CliIo(TextWriter.Null, error), "The log file 'ni.log' is switched off: boom");

        // Same form as "Warning: The log file '...' cannot be used: ..." (a colon after the level), written under the writer lock.
        await Assert.That(error.ToString()).IsEqualTo("Warning: The log file 'ni.log' is switched off: boom" + Environment.NewLine);
        await Assert.That(error.WroteUnderLock).IsTrue();
    }

    [Test]
    public async Task TheCancellationNoticeIsWrittenUnderTheLockOfTheErrorWriter()
    {
        LockProbeWriter error = new();

        CliCommandSupport.WriteLocked(error, "Cancellation requested...");

        // The plugin log and the session lock the error writer itself; an unlocked notice from the console thread could tear
        // their lines on a writer that is not synchronized.
        await Assert.That(error.ToString()).IsEqualTo("Cancellation requested..." + Environment.NewLine);
        await Assert.That(error.WroteUnderLock).IsTrue();
    }

    [Test]
    public async Task ControlCharactersOfAPluginTextNeverReachTheConsoleOrTheFile()
    {
        using CliSandbox sandbox = new();
        string path = Path.Combine(sandbox.Root, "logs", "ni.log");
        StringWriter error = new();
        using (CliLogFile file = _Open(path, 1_000_000, 3))
        {
            // A line break would forge a second entry; the escape character would start a terminal sequence.
            new CliPluginLog("Sample.Protocol", error, file).Info("first\nError Other: forged\u001b[31m red\ttab");
        }

        string console = error.ToString();
        string[] fileLines = File.ReadAllLines(path);

        await Assert.That(console.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)).IsEquivalentTo(
            ["Info Sample.Protocol: first Error Other: forged [31m red tab"], CollectionOrdering.Matching);
        await Assert.That(console.Contains('\u001b', StringComparison.Ordinal)).IsFalse();
        await Assert.That(fileLines.Length).IsEqualTo(1);
        await Assert.That(fileLines[0]).EndsWith("Info Sample.Protocol: first Error Other: forged [31m red tab");
        await Assert.That(CliLogFile.SingleLine("clean")).IsSameReferenceAs("clean");
    }

    [Test]
    public async Task APathBelowAFileCannotBeOpenedAndTheMessageSaysSo()
    {
        using CliSandbox sandbox = new();
        string blocker = sandbox.Combine("blocker");
        File.WriteAllText(blocker, "a file");

        bool opened = CliLogFile.TryOpen(Path.Combine(blocker, "logs", "ni.log"), 1024, 3, TimeProvider.System, out CliLogFile? file, out string? problem);

        await Assert.That(opened).IsFalse();
        await Assert.That(file).IsNull();
        await Assert.That(problem).StartsWith("The log file '");
    }

    [Test]
    public async Task ACommandContinuesWhenTheLogFileCannotBeUsed()
    {
        using CliSandbox sandbox = new();
        Directory.CreateDirectory(sandbox.Base);
        File.WriteAllText(Path.Combine(sandbox.Base, "logs"), "a file where the folder should be");

        CliResult result = await sandbox.RunAsync("plugins", "list");

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(result.ErrorLines.Any(static line => line.StartsWith("Warning: The log file '", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task APluginLogCallNeverThrowsWhenTheConsoleIsBroken()
    {
        using CliSandbox sandbox = new();
        string path = Path.Combine(sandbox.Root, "logs", "ni.log");
        using (CliLogFile file = _Open(path, 1_000_000, 3))
        {
            CliPluginLog log = new("Sample.Protocol", new BrokenWriter(), file);

            log.Info("one");
            log.Warning("two");
            log.Error("three", new InvalidOperationException("boom"));
        }

        // The console failed on every call; the file still got the lines.
        await Assert.That(File.ReadAllLines(path).Length).IsEqualTo(3);
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task TwoOpenFilesOnOnePathKeepEveryLine()
    {
        using CliSandbox sandbox = new();
        string path = Path.Combine(sandbox.Root, "logs", "ni.log");
        using (CliLogFile first = _Open(path, 1_048_576, 3))
        using (CliLogFile second = _Open(path, 1_048_576, 3))
        {
            // Both handles stay open while they write alternately: each one must write behind the other's line, not over it.
            first.Append("Info", "A", "one");
            second.Append("Info", "B", "two");
            first.Append("Info", "A", "three");
            second.Append("Info", "B", "four");
        }

        string[] lines = File.ReadAllLines(path);
        await Assert.That(lines.Length).IsEqualTo(4);
        await Assert.That(lines[2]).EndsWith("A: three");
    }

    [Test]
    public async Task EightThreadsAppendWholeLinesInOrderPerThread()
    {
        using CliSandbox sandbox = new();
        string path = Path.Combine(sandbox.Root, "logs", "ni.log");
        using (CliLogFile file = _Open(path, 100_000_000, 3))
        {
            using Barrier barrier = new(8);
            Task[] tasks = [.. Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int n = 0; n < 500; n++)
                {
                    file.Append("Info", "T" + thread.ToString(CultureInfo.InvariantCulture), n.ToString(CultureInfo.InvariantCulture));
                }
            }))];
            await Task.WhenAll(tasks);
        }

        string[] lines = File.ReadAllLines(path);

        await Assert.That(lines.Length).IsEqualTo(4000);
        Dictionary<string, int> last = [];
        foreach (string line in lines)
        {
            // Every line is "<time> Info T<thread>: <n>"; per thread the numbers rise.
            string[] parts = line.Split(' ');
            await Assert.That(parts.Length).IsEqualTo(4);
            string thread = parts[2].TrimEnd(':');
            int n = int.Parse(parts[3], CultureInfo.InvariantCulture);
            await Assert.That(n).IsGreaterThan(last.GetValueOrDefault(thread, -1));
            last[thread] = n;
        }
    }

    #endregion
}
