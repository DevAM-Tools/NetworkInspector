// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;
using BoundedDecompressor = NetworkInspector.Protocols.Helpers.BoundedDecompressor;
using DecompressionStatus = NetworkInspector.Protocols.Helpers.DecompressionStatus;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the bounded decompression of HTTP/1 bodies (<c>http.max_decompressed_bytes</c>) and for the shared
/// <c>BoundedDecompressor</c>.
/// </summary>
internal sealed class HttpDecompressionTests
{
    private const string _LimitSetting = "http.max_decompressed_bytes";

    private static byte[] _Gzip(byte[] plain)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(plain);
        }

        return output.ToArray();
    }

    private static byte[] _GzipResponse(byte[] plain, string contentType = "application/octet-stream")
    {
        byte[] compressed = _Gzip(plain);
        byte[] head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Encoding: gzip\r\nContent-Length: {compressed.Length}\r\n\r\n");
        return HttpHardeningTests.BuildHttpFrame([.. head, .. compressed]);
    }

    #region Setting

    [Test]
    public async Task SettingHasSecureDefaultAndRange()
    {
        SettingsManager? captured = null;
        using Stack stack = ProtocolTestHelper.BuildStack(settings => captured = settings);

        Setting? setting = captured?.GetSetting(_LimitSetting);

        await Assert.That(setting).IsNotNull();
        await Assert.That(setting!.Value.TryGetAsU64(out ulong value)).IsTrue();
        await Assert.That(value).IsEqualTo(64UL * 1024 * 1024);
        await Assert.That(setting.MinValue!.Value.TryGetAsU64(out ulong min) && min == 1024).IsTrue();
        await Assert.That(setting.MaxValue!.Value.TryGetAsU64(out ulong max) && max == 128UL * 1024 * 1024 * 1024).IsTrue();
    }

    [Test]
    [Arguments(1023UL)]
    [Arguments(0UL)]
    [Arguments(137438953473UL)]
    public async Task SettingOutOfRangeIsRejectedWithTheRange(ulong rejected)
    {
        SettingsManager? captured = null;
        using Stack stack = ProtocolTestHelper.BuildStack(settings => captured = settings);
        Setting setting = captured!.GetSetting(_LimitSetting)!;

        bool valid = setting.TryValidate(SettingValue.U64(rejected), out SettingProblem? problem);

        await Assert.That(valid).IsFalse();
        await Assert.That(problem!.ToString()).Contains("1024").And.Contains("137438953472");
    }

    [Test]
    [Arguments(1024UL)]
    [Arguments(137438953472UL)]
    public async Task SettingBoundariesAreAccepted(ulong accepted)
    {
        SettingsManager? captured = null;
        using Stack stack = ProtocolTestHelper.BuildStack(settings => captured = settings);
        Setting setting = captured!.GetSetting(_LimitSetting)!;

        bool valid = setting.TryValidate(SettingValue.U64(accepted), out _);

        await Assert.That(valid).IsTrue();
    }

    #endregion

    #region Behavior at the limit

    [Test]
    public async Task GzipBodyWithinTheLimitIsDecoded()
    {
        byte[] plain = Encoding.ASCII.GetBytes(new string('a', 4000));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(_GzipResponse(plain));
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "http.payload.decoded", plain).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http.error.decompress_limit").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task GzipBodyOverTheLimitIsNotDecodedAndNamesTheSetting()
    {
        byte[] plain = new byte[200_000];
        byte[] frame = _GzipResponse(plain);

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings((_LimitSetting, SettingValue.U64(1024)));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http.payload.decoded").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.payload").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.error.decompress_limit").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "http.error.decompress_limit", _LimitSetting).ConfigureAwait(false);
    }

    [Test]
    public async Task GzipBodyOfExactlyTheLimitIsDecoded()
    {
        byte[] plain = new byte[1024];
        byte[] frame = _GzipResponse(plain);

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings((_LimitSetting, SettingValue.U64(1024)));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertBytesField(stack, packet, "http.payload.decoded", plain).ConfigureAwait(false);
    }

    [Test]
    public async Task GzipBodyOneByteOverTheLimitIsNotDecoded()
    {
        byte[] frame = _GzipResponse(new byte[1025]);

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings((_LimitSetting, SettingValue.U64(1024)));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http.payload.decoded").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.error.decompress_limit").ConfigureAwait(false);
    }

    [Test]
    public async Task LimitErrorIsRecordedInTheEagerIndex()
    {
        byte[] frame = _GzipResponse(new byte[200_000]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame, (_LimitSetting, SettingValue.U64(1024))).ConfigureAwait(false);
    }

    [Test]
    public async Task CorruptGzipBodyIsNotDecodedAndHasNoLimitError()
    {
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 6\r\n\r\n");
        byte[] frame = HttpHardeningTests.BuildHttpFrame([.. head, 1, 2, 3, 4, 5, 6]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http.payload.decoded").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http.error.decompress_limit").ConfigureAwait(false);
        }
    }

    #endregion

    #region BoundedDecompressor

    [Test]
    public async Task BoundedDecompressorStopsAtTheLimitWithoutReadingEverything()
    {
        // A stream that would produce a huge amount of data: the decompressor must stop long before the end.
        using EndlessStream endless = new();

        DecompressionStatus status = BoundedDecompressor.TryDecompress(endless, 100_000, out ReadOnlyMemory<byte> output);

        await Assert.That(status).IsEqualTo(DecompressionStatus.LimitExceeded);
        await Assert.That(output.Length).IsEqualTo(0);
        await Assert.That(endless.TotalRead < 100_000 + (32 * 1024)).IsTrue();
    }

    [Test]
    public async Task BoundedDecompressorAcceptsEmptyOutput()
    {
        using MemoryStream empty = new();

        DecompressionStatus status = BoundedDecompressor.TryDecompress(empty, 10, out ReadOnlyMemory<byte> output);

        await Assert.That(status).IsEqualTo(DecompressionStatus.Succeeded);
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    public async Task BoundedDecompressorRejectsNullStream()
    {
        await Assert.That(() => BoundedDecompressor.TryDecompress(null!, 10, out _)).Throws<ArgumentNullException>();
    }

    private sealed class EndlessStream : Stream
    {
        public long TotalRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            TotalRead += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion
}
