// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Exit-code and smoke tests for <see cref="ConvertCommand"/> and <see cref="ExportCommand"/>.
/// </summary>
internal sealed class CommandRunTests
{
    [Test]
    public async Task Convert_InvalidProfileName_ReturnsArgumentError()
    {
        int code = ConvertCommand.Run([
            "random:count=1,mode=udp4",
            "-o", "out.pcapng",
            "--profile", "../evil",
        ]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Convert_WithProfile_Succeeds()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ni-prof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "out.pcapng");
        try
        {
            int code = ConvertCommand.Run([
                "random:count=2,mode=udp4",
                "-o", path,
                "-n", "2",
                "--settings-path", dir,
                "--profile", "TestProfile",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    public async Task Export_InvalidMaxPackets_ReturnsArgumentError()
    {
        int code = ExportCommand.Run(["random:count=1,mode=udp4", "-n", "abc"]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Export_MissingFormatValue_ReturnsArgumentError()
    {
        int code = ExportCommand.Run(["random:count=1,mode=udp4", "-f"]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Convert_MissingOutputValue_ReturnsArgumentError()
    {
        int code = ConvertCommand.Run(["random:count=1,mode=udp4", "-o"]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Convert_MissingOutputOption_ReturnsArgumentError()
    {
        int code = ConvertCommand.Run(["random:count=1,mode=udp4"]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Export_BlfCacheTooLarge_ReturnsArgumentError()
    {
        int code = ExportCommand.Run([
            "random:count=1,mode=udp4",
            "-f", "json",
            "-o", Path.Combine(Path.GetTempPath(), $"ni-blf-cache-{Guid.NewGuid():N}.json"),
            "--blf-cache-size", "999999999999",
        ]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Export_MissingOutput_ReturnsArgumentError()
    {
        int code = ExportCommand.Run([
            "random:count=1,mode=udp4",
            "-f", "json",
        ]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Export_StdoutDash_ReturnsArgumentError()
    {
        int code = ExportCommand.Run([
            "random:count=1,mode=udp4",
            "-f", "json",
            "-o", "-",
        ]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Convert_StdoutDash_ReturnsArgumentError()
    {
        int code = ConvertCommand.Run([
            "random:count=1,mode=udp4",
            "-o", "-",
        ]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Export_RandomToJsonFile_Succeeds()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ni-export-{Guid.NewGuid():N}.json");
        try
        {
            int code = ExportCommand.Run([
                "random:count=3,mode=udp4",
                "-f", "json:style=compact",
                "-o", path,
                "-n", "3",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Test]
    public async Task Export_SplitCount_WritesNumberedFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ni-export-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string basePath = Path.Combine(dir, "part.json");
        try
        {
            int code = ExportCommand.Run([
                "random:count=6,mode=udp4",
                "-f", "json:style=compact",
                "-o", basePath,
                "--split-count", "2",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(Path.Combine(dir, "part_00001.json"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "part_00002.json"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "part_00003.json"))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    public async Task Export_Parquet_SplitCount_WritesNumberedDirectories()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ni-export-parquet-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string basePath = Path.Combine(dir, "dataset");
        try
        {
            int code = ExportCommand.Run([
                "random:count=6,mode=udp4",
                "-f", "parquet",
                "-o", basePath,
                "--split-count", "2",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(Directory.Exists(Path.Combine(dir, "dataset_00001"))).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(dir, "dataset_00002"))).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(dir, "dataset_00003"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "dataset_00001", "packets.parquet"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "dataset_00002", "packets.parquet"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "dataset_00003", "packets.parquet"))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    public async Task Export_DuckDb_SplitCount_WritesNumberedFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ni-export-duckdb-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string basePath = Path.Combine(dir, "part.duckdb");
        try
        {
            int code = ExportCommand.Run([
                "random:count=6,mode=udp4",
                "-f", "duckdb",
                "-o", basePath,
                "--split-count", "2",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(Path.Combine(dir, "part_00001.duckdb"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "part_00002.duckdb"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "part_00003.duckdb"))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    public async Task Export_Parquet_WithoutSplit_WritesSingleDataset()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ni-export-parquet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string basePath = Path.Combine(dir, "out_parquet");
        try
        {
            int code = ExportCommand.Run([
                "random:count=3,mode=udp4",
                "-f", "parquet",
                "-o", basePath,
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(Directory.Exists(basePath)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(basePath, "packets.parquet"))).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(dir, "out_parquet_00001"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    public async Task Convert_RandomToTempPcapng_Succeeds()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ni-convert-{Guid.NewGuid():N}.pcapng");
        try
        {
            int code = ConvertCommand.Run([
                "random:count=5,mode=udp4",
                "-o", path,
                "-n", "5",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Test]
    public async Task Convert_SplitCount_WritesNumberedFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ni-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string basePath = Path.Combine(dir, "part.pcapng");
        try
        {
            int code = ConvertCommand.Run([
                "random:count=6,mode=udp4",
                "-o", basePath,
                "--split-count", "2",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(Path.Combine(dir, "part_00001.pcapng"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "part_00002.pcapng"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "part_00003.pcapng"))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    public async Task Convert_SplitSizeOverflow_ReturnsArgumentError()
    {
        int code = ConvertCommand.Run([
            "random:count=1,mode=udp4",
            "-o", "out.pcapng",
            "--split-size", "999999999999999999",
        ]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Export_MissingSourceFile_ReturnsSourceOpenError()
    {
        int code = ExportCommand.Run([
            "definitely-missing-xyz.pcapng",
            "-f", "json",
            "-o", Path.Combine(Path.GetTempPath(), $"ni-missing-{Guid.NewGuid():N}.json"),
        ]);

        await Assert.That(code).IsEqualTo((int)ExitCode.SourceOpenError);
    }

    [Test]
    public async Task CliEntry_ExportInvalidNumeric_ReturnsArgumentError()
    {
        int code = CliEntry.Run(["export", "random:count=1,mode=udp4", "-n", "nope"]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }
}
