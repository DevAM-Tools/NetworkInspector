// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Unit tests for <see cref="SourceConfig.Parse"/> and validation.
/// </summary>
internal sealed class SourceConfigTests
{
    [Test]
    public async Task Parse_BarePcapngPath_ReturnsPcapSourceConfig()
    {
        SourceConfig config = SourceConfig.Parse("capture.pcapng");

        await Assert.That(config).IsTypeOf<PcapSourceConfig>();
        await Assert.That(((PcapSourceConfig)config).Path).IsEqualTo("capture.pcapng");
    }

    [Test]
    public async Task Parse_BareBlfPath_ReturnsBlfSourceConfig()
    {
        SourceConfig config = SourceConfig.Parse("data.blf");

        await Assert.That(config).IsTypeOf<BlfSourceConfig>();
    }

    [Test]
    public async Task Parse_BareAscPath_ReturnsAscSourceConfig()
    {
        SourceConfig config = SourceConfig.Parse("log.asc");

        await Assert.That(config).IsTypeOf<AscSourceConfig>();
    }

    [Test]
    public async Task Parse_TypedPcapSpec_ReturnsPcapSourceConfig()
    {
        SourceConfig config = SourceConfig.Parse("pcap:path=file.pcap");

        await Assert.That(config).IsTypeOf<PcapSourceConfig>();
        await Assert.That(((PcapSourceConfig)config).Path).IsEqualTo("file.pcap");
    }

    [Test]
    public async Task Parse_RandomSpec_ReturnsRandomSourceConfig()
    {
        SourceConfig config = SourceConfig.Parse("random:count=10,seed=7,mode=udp4");

        await Assert.That(config).IsTypeOf<RandomSourceConfig>();
        RandomSourceConfig random = (RandomSourceConfig)config;
        await Assert.That(random.Count).IsEqualTo(10);
        await Assert.That(random.Seed).IsEqualTo(7UL);
        await Assert.That(random.Mode).IsEqualTo(RandomFrameMode.UdpIPv4);
    }

    [Test]
    public async Task Parse_UnknownExtension_ThrowsArgumentException()
    {
        await Assert.That(() => SourceConfig.Parse("file.xyz"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Parse_ShellMetacharacter_ThrowsArgumentException()
    {
        await Assert.That(() => SourceConfig.Parse("a.pcapng|whoami"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Parse_UnknownType_ThrowsArgumentException()
    {
        await Assert.That(() => SourceConfig.Parse("foo:path=bar"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ValidateBeforeStart_MissingPcapFile_ThrowsArgumentException()
    {
        PcapSourceConfig config = new("definitely-missing-file-xyz.pcapng");

        await Assert.That(() => config.ValidateBeforeStart())
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Parse_RandomInvalidCount_ThrowsArgumentException()
    {
        await Assert.That(() => SourceConfig.Parse("random:count=-1"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Parse_RandomUnknownMode_ThrowsArgumentException()
    {
        await Assert.That(() => SourceConfig.Parse("random:mode=notamode"))
            .Throws<ArgumentException>();
    }
}
