// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// The default string representation of <see cref="Field"/> and <see cref="MutField"/>: the default display line. A custom text
/// is the whole line, a field without a value shows its UI name, every other field shows UI name, <c>": "</c>, and the
/// value's default string. The character, UTF-8, and string paths of both cursors agree, and no formatting member throws.
/// </summary>
internal sealed class FieldStringRepresentationTests
{
    #region Helpers

    /// <summary>Builds a stack with the probe protocol and parses one frame through it.</summary>
    /// <param name="snapshotCursors">Whether the protocol formats every cursor it appends (see <see cref="LineProtocol"/>).</param>
    /// <param name="mode">Whether the parse keeps a field tree.</param>
    /// <returns>The stack (the caller disposes it), the parsed packet, and the protocol that recorded the cursors.</returns>
    private static (Stack Stack, Packet Packet, LineProtocol Protocol) _Parse(bool snapshotCursors, FieldTreeMode mode = FieldTreeMode.Build)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        LineProtocol protocol = new()
        {
            SnapshotCursors = snapshotCursors,
        };
        ProtocolId id = builder.RegisterProtocol(protocol);
        protocol.RegisterFields(builder, id);
        Stack stack = builder.Build();
        ParseOptions options = new(mode, firstProtocol: id);
        Frame frame = Frame.Create(
            0, Timestamp.FromSecs(0), new byte[4], LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        if (!Packet.TryParse(0, stack, frame, in options, out Packet? packet, out ParseFailure failure) || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return (stack, packet, protocol);
    }

    /// <summary>Finds the first field of the packet with the given id.</summary>
    /// <param name="packet">The packet to search.</param>
    /// <param name="id">The field id.</param>
    /// <returns>The field; default when the packet holds none.</returns>
    private static Field _Find(Packet packet, FieldId id)
    {
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id)
            {
                return field;
            }
        }

        return default;
    }

    /// <summary>Finds a probe field by its registered name.</summary>
    /// <param name="stack">The stack that registered the probe protocol.</param>
    /// <param name="packet">The parsed packet.</param>
    /// <param name="name">The machine-readable field name, for example <c>disptest.port</c>.</param>
    /// <returns>The field; default when the name is unknown or the packet holds none.</returns>
    private static Field _Named(Stack stack, Packet packet, string name)
    {
        FieldId? id = stack.GetFieldId(name);
        return id is { } found
            ? _Find(packet, found)
            : default;
    }

    /// <summary>
    /// Formats the field with every path and collects the results; no async state machine runs here, so the allocation count of
    /// the callers is not disturbed.
    /// </summary>
    /// <param name="field">The field to format.</param>
    /// <returns>The results of the three paths.</returns>
    private static PathResults _FormatAllPaths(Field field)
    {
        Span<char> chars = stackalloc char[256];
        bool charsFit = field.TryFormat(chars, out int charsWritten, default, CultureInfo.InvariantCulture);
        Span<byte> bytes = stackalloc byte[512];
        bool bytesFit = field.TryFormat(bytes, out int bytesWritten, default, CultureInfo.InvariantCulture);
        return new PathResults(
            field.ToString(null, CultureInfo.InvariantCulture),
            charsFit,
            new string(chars[..charsWritten]),
            bytesFit,
            Encoding.UTF8.GetString(bytes[..bytesWritten]));
    }

    /// <summary>Formats a field into a destination of the given length with the character path.</summary>
    /// <param name="field">The field to format.</param>
    /// <param name="length">The destination length in characters.</param>
    /// <returns>Whether the line fit, the characters written, and the text.</returns>
    private static (bool Fits, int Written, string Text) _FormatChars(Field field, int length)
    {
        char[] buffer = new char[length];
        bool fits = field.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        return (fits, written, new string(buffer, 0, written));
    }

    /// <summary>Formats a field into a destination of the given length with the UTF-8 path.</summary>
    /// <param name="field">The field to format.</param>
    /// <param name="length">The destination length in bytes.</param>
    /// <returns>Whether the line fit, the bytes written, and the decoded text.</returns>
    private static (bool Fits, int Written, string Text) _FormatUtf8(Field field, int length)
    {
        byte[] buffer = new byte[length];
        bool fits = field.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        return (fits, written, Encoding.UTF8.GetString(buffer, 0, written));
    }

    /// <summary>Allocated bytes of repeated formatting of the given fields on the character and UTF-8 paths, after a warm-up round.</summary>
    /// <param name="fields">The fields to format.</param>
    /// <param name="rounds">How often every field is formatted on each path.</param>
    /// <returns>The bytes the calling thread allocated during the measured rounds.</returns>
    private static long _MeasureFormatAllocations(Field[] fields, int rounds)
    {
        Span<char> chars = stackalloc char[256];
        Span<byte> bytes = stackalloc byte[512];

        // Warm-up: the first round compiles the paths and builds and caches every deferred text, which are one-time costs.
        for (int i = 0; i < fields.Length; i++)
        {
            _ = fields[i].TryFormat(chars, out _, default, CultureInfo.InvariantCulture);
            _ = fields[i].TryFormat(bytes, out _, default, CultureInfo.InvariantCulture);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < rounds; round++)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                _ = fields[i].TryFormat(chars, out _, default, CultureInfo.InvariantCulture);
                _ = fields[i].TryFormat(bytes, out _, default, CultureInfo.InvariantCulture);
            }
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Formats a default (invalid) cursor with every path of <see cref="MutField"/>; a ref struct cannot live across an await.</summary>
    /// <returns>The empty-line results.</returns>
    private static (string Text, bool CharsFit, int Chars, bool BytesFit, int Bytes) _FormatDefaultCursor()
    {
        MutField cursor = default;
        Span<char> chars = stackalloc char[8];
        bool charsFit = cursor.TryFormat(chars, out int charsWritten, default, CultureInfo.InvariantCulture);
        Span<byte> bytes = stackalloc byte[8];
        bool bytesFit = cursor.TryFormat(bytes, out int bytesWritten, default, CultureInfo.InvariantCulture);
        return (cursor.ToString(null, CultureInfo.InvariantCulture), charsFit, charsWritten, bytesFit, bytesWritten);
    }

    /// <summary>The line of every path of one field, as strings and flags.</summary>
    /// <param name="Text"><c>ToString()</c>.</param>
    /// <param name="CharsFit">Whether the 256-character destination fit.</param>
    /// <param name="Chars">The characters the character path wrote.</param>
    /// <param name="BytesFit">Whether the 512-byte destination fit.</param>
    /// <param name="Utf8">The text the UTF-8 path wrote, decoded.</param>
    private readonly record struct PathResults(string Text, bool CharsFit, string Chars, bool BytesFit, string Utf8);

    #endregion

    #region Display line rule

    // Sync: EXPORTER_GUIDE.md § Rebuilding the display line (the three cases). The test reads no Markdown; the strings below are
    // a copy of the guide's examples.

    /// <summary>
    /// The default line of every case of the rule: custom text alone, representation after the label, value after the label,
    /// label alone, empty text, a representation on a value of type None, non-ASCII text, and a float.
    /// </summary>
    [Test]
    [Arguments("disptest.length", "Length: 8 bytes")]
    [Arguments("disptest.echo", "UI: Echo")]
    [Arguments("disptest.port", "Source Port: 53")]
    [Arguments("disptest.container", "Container")]
    [Arguments("disptest.empty", "")]
    [Arguments("disptest.named", "Custom Container")]
    [Arguments("disptest.hidden", "Hidden Representation")]
    [Arguments("disptest.wide", "Größe: ü")]
    [Arguments("disptest.ratio", "Ratio: 0.5")]
    public async Task DefaultLineFollowsTheThreeCaseRule(string fieldName, string expectedLine)
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field field = _Named(stack, packet, fieldName);

            string line = field.ToString(null, CultureInfo.InvariantCulture);

            await Assert.That(field.IsValid).IsTrue();
            await Assert.That(line).IsEqualTo(expectedLine);
        }
    }

    /// <summary>A custom text is the whole line; the label is not written in front of it a second time.</summary>
    [Test]
    public async Task CustomTextIsNeverPrefixedWithTheUiName()
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field field = _Named(stack, packet, "disptest.length");

            string line = field.ToString(null, CultureInfo.InvariantCulture);

            await Assert.That(line.StartsWith("Length: Length", StringComparison.Ordinal)).IsFalse();
            await Assert.That(line).IsEqualTo(field.CustomText.AsString);
        }
    }

    /// <summary>
    /// <c>FieldValue.ToString()</c> stays the value part (representation, else data text) and <c>Data.ToString()</c> stays the
    /// default text of the data, with and without a custom representation.
    /// </summary>
    [Test]
    [Arguments("disptest.length", "8", "8")]
    [Arguments("disptest.echo", "Echo", "5")]
    [Arguments("disptest.port", "53", "53")]
    [Arguments("disptest.container", "", "")]
    public async Task ValueStringsStayTheValuePartAndTheDataText(string fieldName, string expectedValueText, string expectedDataText)
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field field = _Named(stack, packet, fieldName);

            string valueText = field.Value.ToString(null, CultureInfo.InvariantCulture);
            string dataText = field.Value.Data.ToString(null, CultureInfo.InvariantCulture);

            await Assert.That(valueText).IsEqualTo(expectedValueText);
            await Assert.That(dataText).IsEqualTo(expectedDataText);
        }
    }

    /// <summary>Every part of a line (label, value data, representation, custom text) is reachable, so a caller can build an own line.</summary>
    [Test]
    public async Task EveryPartOfALineStaysReadableForAnOwnLine()
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field echo = _Named(stack, packet, "disptest.echo");
            Field length = _Named(stack, packet, "disptest.length");
            Field port = _Named(stack, packet, "disptest.port");

            string own = Invariant($"{echo.FieldInfo?.Name}={echo.Value.Data}");

            await Assert.That(own).IsEqualTo("disptest.echo=5");
            await Assert.That(echo.FieldInfo?.UiName).IsEqualTo("UI");
            await Assert.That(echo.Value.CustomRepresentation.AsString).IsEqualTo("Echo");
            await Assert.That(echo.CustomText.IsNull).IsTrue();
            await Assert.That(length.CustomText.AsString).IsEqualTo("Length: 8 bytes");
            await Assert.That(length.Value.CustomRepresentation.IsNull).IsTrue();
            await Assert.That(port.CustomText.IsNull).IsTrue();
            await Assert.That(port.Value.CustomRepresentation.IsNull).IsTrue();
        }
    }

    /// <summary>
    /// The registered dissectors follow the rule: the <c>udp</c> layer carries its summary as custom text, which is the whole line,
    /// and <c>udp.length</c> has none, so its line is label, separator, and value.
    /// </summary>
    [Test]
    public async Task TheUdpDissectorLinesFollowTheRule()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();
        ParseOptions options = new(FieldTreeMode.Build);
        Frame frame = Frame.Create(
            0,
            Timestamp.FromSecs(0),
            FrameBuilders.GenerateStaticUdpIpv6Frame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        bool parsed = Packet.TryParse(0, stack, frame, in options, out Packet? packet, out ParseFailure failure);
        await Assert.That(parsed).IsTrue().Because(failure.ToString());
        Field layer = _Find(packet!, stack.GetFieldId("udp")!.Value);
        Field length = _Find(packet!, stack.GetFieldId("udp.length")!.Value);

        string layerLine = layer.ToString(null, CultureInfo.InvariantCulture);
        string lengthLine = length.ToString(null, CultureInfo.InvariantCulture);

        await Assert.That(layer.CustomText.IsNull).IsFalse();
        await Assert.That(layerLine).IsEqualTo(layer.CustomText.AsString);
        await Assert.That(length.CustomText.IsNull).IsTrue();
        await Assert.That(lengthLine).IsEqualTo(string.Concat("Length: ", length.Value.ToString(null, CultureInfo.InvariantCulture)));
    }

    #endregion

    #region Format paths

    /// <summary>The string, interpolation, character, and UTF-8 paths give one line, into an exactly sized destination.</summary>
    [Test]
    [Arguments("disptest.length", "Length: 8 bytes")]
    [Arguments("disptest.echo", "UI: Echo")]
    [Arguments("disptest.port", "Source Port: 53")]
    [Arguments("disptest.container", "Container")]
    [Arguments("disptest.wide", "Größe: ü")]
    [Arguments("disptest.ratio", "Ratio: 0.5")]
    public async Task CharUtf8AndStringPathsAgree(string fieldName, string expectedLine)
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field field = _Named(stack, packet, fieldName);

            PathResults paths = _FormatAllPaths(field);
            (bool exactFits, int exactWritten, string exactText) = _FormatChars(field, expectedLine.Length);
            (bool utf8Fits, int utf8Written, string utf8Text) = _FormatUtf8(field, Encoding.UTF8.GetByteCount(expectedLine));
            string interpolated = Invariant($"{field}");
            string created = string.Create(CultureInfo.InvariantCulture, $"{field}");

            await Assert.That(paths.Text).IsEqualTo(expectedLine);
            await Assert.That(paths.CharsFit).IsTrue();
            await Assert.That(paths.Chars).IsEqualTo(expectedLine);
            await Assert.That(paths.BytesFit).IsTrue();
            await Assert.That(paths.Utf8).IsEqualTo(expectedLine);
            await Assert.That(exactFits).IsTrue();
            await Assert.That(exactWritten).IsEqualTo(expectedLine.Length);
            await Assert.That(exactText).IsEqualTo(expectedLine);
            await Assert.That(utf8Fits).IsTrue();
            await Assert.That(utf8Written).IsEqualTo(Encoding.UTF8.GetByteCount(expectedLine));
            await Assert.That(utf8Text).IsEqualTo(expectedLine);
            await Assert.That(interpolated).IsEqualTo(expectedLine);
            await Assert.That(created).IsEqualTo(expectedLine);
        }
    }

    /// <summary>A destination one character (one byte) too small reports false and 0 written on the character and UTF-8 paths.</summary>
    [Test]
    [Arguments("disptest.length")]
    [Arguments("disptest.echo")]
    [Arguments("disptest.port")]
    [Arguments("disptest.container")]
    [Arguments("disptest.wide")]
    [Arguments("disptest.ratio")]
    public async Task ADestinationThatIsOneUnitTooSmallFailsCleanly(string fieldName)
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field field = _Named(stack, packet, fieldName);
            string line = field.ToString(null, CultureInfo.InvariantCulture);
            int utf8Length = Encoding.UTF8.GetByteCount(line);

            (bool charsFit, int charsWritten, _) = _FormatChars(field, line.Length - 1);
            (bool utf8Fits, int utf8Written, _) = _FormatUtf8(field, utf8Length - 1);

            await Assert.That(charsFit).IsFalse();
            await Assert.That(charsWritten).IsEqualTo(0);
            await Assert.That(utf8Fits).IsFalse();
            await Assert.That(utf8Written).IsEqualTo(0);
        }
    }

    /// <summary>
    /// A destination that cannot hold the label, the label and the separator, or the value after them fails cleanly: false and 0
    /// written on both paths (the line <c>Source Port: 53</c> needs 15 units: label 11, separator 2, value 2).
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(5)]
    [Arguments(11)]
    [Arguments(12)]
    [Arguments(13)]
    [Arguments(14)]
    public async Task ADestinationShorterThanTheLineFailsCleanly(int length)
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field field = _Named(stack, packet, "disptest.port");

            (bool charsFit, int charsWritten, _) = _FormatChars(field, length);
            (bool utf8Fits, int utf8Written, _) = _FormatUtf8(field, length);

            await Assert.That(charsFit).IsFalse();
            await Assert.That(charsWritten).IsEqualTo(0);
            await Assert.That(utf8Fits).IsFalse();
            await Assert.That(utf8Written).IsEqualTo(0);
        }
    }

    /// <summary>A set but empty custom text is an empty line that fits an empty destination.</summary>
    [Test]
    public async Task AnEmptyLineFitsAnEmptyDestination()
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field empty = _Named(stack, packet, "disptest.empty");

            (bool charsFit, int charsWritten, _) = _FormatChars(empty, 0);
            (bool utf8Fits, int utf8Written, _) = _FormatUtf8(empty, 0);

            await Assert.That(charsFit).IsTrue();
            await Assert.That(charsWritten).IsEqualTo(0);
            await Assert.That(utf8Fits).IsTrue();
            await Assert.That(utf8Written).IsEqualTo(0);
        }
    }

    /// <summary>A deferred custom text is built by the first read of any path and shared by every other path.</summary>
    [Test]
    public async Task ADeferredCustomTextIsBuiltOnceAcrossAllFormatPaths()
    {
        (Stack stack, Packet packet, LineProtocol protocol) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field field = _Named(stack, packet, "disptest.deferred");
            int callsBeforeAnyRead = protocol.FactoryCalls;

            string text = field.ToString(null, CultureInfo.InvariantCulture);
            PathResults paths = _FormatAllPaths(field);
            string interpolated = Invariant($"{field}");
            string again = field.ToString(null, CultureInfo.InvariantCulture);

            await Assert.That(callsBeforeAnyRead).IsEqualTo(0);
            await Assert.That(protocol.FactoryCalls).IsEqualTo(1);
            await Assert.That(text).IsEqualTo("Deferred: 42");
            await Assert.That(paths.Chars).IsEqualTo(text);
            await Assert.That(paths.Utf8).IsEqualTo(text);
            await Assert.That(interpolated).IsEqualTo(text);
            await Assert.That(ReferenceEquals(text, again)).IsTrue();
        }
    }

    /// <summary>The character and UTF-8 paths allocate nothing once the deferred texts are built.</summary>
    [Test]
    public async Task TryFormatAllocatesNothing()
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field[] fields =
            [
                _Named(stack, packet, "disptest.length"),
                _Named(stack, packet, "disptest.echo"),
                _Named(stack, packet, "disptest.port"),
                _Named(stack, packet, "disptest.container"),
                _Named(stack, packet, "disptest.ratio"),
                _Named(stack, packet, "disptest.deferred"),
            ];

            long allocated = _MeasureFormatAllocations(fields, rounds: 1_000);

            await Assert.That(allocated).IsEqualTo(0L);
        }
    }

    /// <summary>Eight threads reading the line of one sealed field (the first read of a deferred text racing) all get the same line.</summary>
    [Test]
    public async Task EightThreadsFormattingOneSealedFieldAllGetTheSameLine()
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false);
        using (stack)
        {
            Field field = _Named(stack, packet, "disptest.deferred");
            const int readerCount = 8;
            string[] seen = new string[readerCount];

            // Dedicated threads, not pool tasks: all eight must be alive at the barrier together, so the first reads of the one
            // deferred text race on the sealed packet.
            using Barrier barrier = new(readerCount);
            Thread[] readers = new Thread[readerCount];
            for (int i = 0; i < readerCount; i++)
            {
                int slot = i;
                readers[i] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    seen[slot] = (slot % 2 == 0)
                        ? field.ToString(null, CultureInfo.InvariantCulture)
                        : _FormatAllPaths(field).Chars;
                })
                {
                    Name = "field-line-reader",
                };
                readers[i].Start();
            }

            foreach (Thread reader in readers)
            {
                reader.Join();
            }

            await Assert.That(seen.All(line => line == "Deferred: 42")).IsTrue();
        }
    }

    #endregion

    #region Default texts

    // Sync: EXPORTER_GUIDE.md § Rebuilding the display line, default text table (the same strings; this test reads no Markdown).

    /// <summary>One value of every field type with the default text that the exporter guide documents for it.</summary>
    /// <returns>The cases, as factories (a <see cref="FieldValue"/> is not a constant).</returns>
    public static IEnumerable<Func<(string Name, FieldValue Value, string Expected)>> DefaultTextCases()
    {
        yield return static () => ("Bool", FieldValue.NewBool(true), "True");
        yield return static () => ("I64", FieldValue.NewI64(-5), "-5");
        yield return static () => ("U64", FieldValue.NewU64(53), "53");
        yield return static () => ("F64", FieldValue.NewF64(0.5), "0.5");
        yield return static () => ("String", FieldValue.NewString("verbatim: ü"), "verbatim: ü");
        yield return static () => ("Bytes", FieldValue.NewBytes(new byte[] { 0x0A, 0xFF, 0x00 }), "0A FF 00");
        yield return static () => ("MacAddress", FieldValue.NewMacAddress(new MacAddress(0x001A2B3C4D5EUL)), "00:1A:2B:3C:4D:5E");
        yield return static () => ("IPv4", FieldValue.NewIPv4(new IPv4Address(0xC0A80101U)), "192.168.1.1");
        yield return static () => ("IPv6", FieldValue.NewIPv6(new IPv6Address(0x20010DB800000000UL, 1UL)), "2001:DB8::1");
        yield return static () => ("Eui64", FieldValue.NewEui64(new Eui64(0x001A2BFFFE3C4D5EUL)), "00:1A:2B:FF:FE:3C:4D:5E");
        yield return static () => (
            "Uuid", FieldValue.NewUuid(new Uuid(0x123E4567E89B12D3UL, 0xA456426614174000UL)), "123E4567-E89B-12D3-A456-426614174000");
        yield return static () => ("Timestamp", FieldValue.NewTimestamp(Timestamp.FromSecs(1_767_225_600L)), "2026-01-01T00:00:00.000000000Z");
        yield return static () => ("None", FieldValue.None, string.Empty);
    }

    /// <summary>The default text of every field type (the strings of the exporter guide's default-text table).</summary>
    /// <param name="testCase">The field type name, a value of it, and the expected text.</param>
    [Test]
    [MethodDataSource(nameof(DefaultTextCases))]
    public async Task EveryFieldTypeHasItsDocumentedDefaultText((string Name, FieldValue Value, string Expected) testCase)
    {
        string valueText = testCase.Value.ToString(null, CultureInfo.InvariantCulture);
        string dataText = testCase.Value.Data.ToString(null, CultureInfo.InvariantCulture);

        await Assert.That(valueText).IsEqualTo(testCase.Expected);
        await Assert.That(dataText).IsEqualTo(testCase.Expected);
    }

    #endregion

    #region Cursor and field agree

    /// <summary>The cursor read inside the dissector gives, on every path, the line that the field gives after the parse.</summary>
    [Test]
    [Arguments("disptest.length")]
    [Arguments("disptest.echo")]
    [Arguments("disptest.port")]
    [Arguments("disptest.container")]
    [Arguments("disptest.empty")]
    [Arguments("disptest.named")]
    [Arguments("disptest.hidden")]
    [Arguments("disptest.wide")]
    [Arguments("disptest.ratio")]
    [Arguments("disptest.deferred")]
    public async Task ACursorInsideADissectorFormatsLikeTheFieldAfterTheParse(string fieldName)
    {
        (Stack stack, Packet packet, LineProtocol protocol) = _Parse(snapshotCursors: true);
        using (stack)
        {
            FieldId id = stack.GetFieldId(fieldName)!.Value;
            Field field = _Find(packet, id);
            CursorLine cursor = protocol.Cursors[id.Value];

            PathResults after = _FormatAllPaths(field);

            await Assert.That(cursor.Text).IsEqualTo(after.Text);
            await Assert.That(cursor.CharsFit).IsTrue();
            await Assert.That(cursor.Chars).IsEqualTo(after.Text);
            await Assert.That(cursor.Utf8Fits).IsTrue();
            await Assert.That(cursor.Utf8).IsEqualTo(after.Text);
        }
    }

    /// <summary>
    /// A field id that the stack does not know has no label: its line is the value part alone (no label, no separator), the empty
    /// line without a value, and a custom text still wins; the cursor and every path agree.
    /// </summary>
    [Test]
    public async Task AFieldIdThatTheStackDoesNotKnowHasNoLabel()
    {
        (Stack stack, Packet packet, LineProtocol protocol) = _Parse(snapshotCursors: true);
        using (stack)
        {
            Field withoutValue = _Find(packet, protocol.UnknownWithoutValue);
            Field withValue = _Find(packet, protocol.UnknownWithValue);
            Field withText = _Find(packet, protocol.UnknownWithText);
            CursorLine cursorWithoutValue = protocol.Cursors[protocol.UnknownWithoutValue.Value];
            CursorLine cursorWithValue = protocol.Cursors[protocol.UnknownWithValue.Value];
            CursorLine cursorWithText = protocol.Cursors[protocol.UnknownWithText.Value];

            PathResults valuePaths = _FormatAllPaths(withValue);
            (bool tooSmallChars, int tooSmallCharsWritten, _) = _FormatChars(withValue, 0);
            (bool tooSmallUtf8, int tooSmallUtf8Written, _) = _FormatUtf8(withValue, 0);

            await Assert.That(withoutValue.FieldInfo).IsNull();
            await Assert.That(withoutValue.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo(string.Empty);
            await Assert.That(withValue.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("7");
            await Assert.That(withText.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("Own text");
            await Assert.That(valuePaths.Chars).IsEqualTo("7");
            await Assert.That(valuePaths.Utf8).IsEqualTo("7");
            await Assert.That(tooSmallChars).IsFalse();
            await Assert.That(tooSmallCharsWritten).IsEqualTo(0);
            await Assert.That(tooSmallUtf8).IsFalse();
            await Assert.That(tooSmallUtf8Written).IsEqualTo(0);
            await Assert.That(cursorWithoutValue.Text).IsEqualTo(string.Empty);
            await Assert.That(cursorWithoutValue.Chars).IsEqualTo(string.Empty);
            await Assert.That(cursorWithValue.Text).IsEqualTo("7");
            await Assert.That(cursorWithValue.Chars).IsEqualTo("7");
            await Assert.That(cursorWithValue.Utf8).IsEqualTo("7");
            await Assert.That(cursorWithText.Text).IsEqualTo("Own text");
        }
    }

    /// <summary>A default field and a default cursor give the empty line on every path and never throw.</summary>
    [Test]
    public async Task ADefaultFieldAndADefaultCursorGiveTheEmptyLineAndNeverThrow()
    {
        Field field = default;
        PathResults fieldPaths = _FormatAllPaths(field);
        (bool charsFit, int charsWritten, _) = _FormatChars(field, 0);
        (string text, bool cursorCharsFit, int cursorChars, bool cursorBytesFit, int cursorBytes) = _FormatDefaultCursor();

        await Assert.That(field.IsValid).IsFalse();
        await Assert.That(fieldPaths.Text).IsEqualTo(string.Empty);
        await Assert.That(fieldPaths.CharsFit).IsTrue();
        await Assert.That(fieldPaths.Chars).IsEqualTo(string.Empty);
        await Assert.That(fieldPaths.BytesFit).IsTrue();
        await Assert.That(fieldPaths.Utf8).IsEqualTo(string.Empty);
        await Assert.That(charsFit).IsTrue();
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(text).IsEqualTo(string.Empty);
        await Assert.That(cursorCharsFit).IsTrue();
        await Assert.That(cursorChars).IsEqualTo(0);
        await Assert.That(cursorBytesFit).IsTrue();
        await Assert.That(cursorBytes).IsEqualTo(0);
    }

    #endregion

    #region Skip-tree fields

    /// <summary>A child of a skip-tree packet shows its label alone (no throw) and its <c>Try</c> readers miss; the root stays readable.</summary>
    [Test]
    public async Task ASkipTreeChildShowsItsLabelAloneAndTheRootStaysReadable()
    {
        (Stack stack, Packet packet, LineProtocol protocol) = _Parse(snapshotCursors: true, FieldTreeMode.Skip);
        using (stack)
        {
            FieldId portId = stack.GetFieldId("disptest.port")!.Value;
            FieldId lengthId = stack.GetFieldId("disptest.length")!.Value;
            CursorLine portCursor = protocol.Cursors[portId.Value];
            CursorLine lengthCursor = protocol.Cursors[lengthId.Value];

            // A handle for a child of a packet that keeps no tree: the formatting members must not throw for it.
            Field child = new(packet, 5, portId);
            PathResults childPaths = _FormatAllPaths(child);
            bool childValueRead = child.TryGetValue(out _);
            bool childTextRead = child.TryGetCustomText(out _);
            Field root = packet.RootField();

            await Assert.That(packet.HasFieldTree).IsFalse();
            await Assert.That(portCursor.Text).IsEqualTo("Source Port");
            await Assert.That(portCursor.Chars).IsEqualTo("Source Port");
            await Assert.That(portCursor.Utf8).IsEqualTo("Source Port");
            await Assert.That(lengthCursor.Text).IsEqualTo("Length");
            await Assert.That(childPaths.Text).IsEqualTo("Source Port");
            await Assert.That(childPaths.Chars).IsEqualTo("Source Port");
            await Assert.That(childPaths.Utf8).IsEqualTo("Source Port");
            await Assert.That(childValueRead).IsFalse();
            await Assert.That(childTextRead).IsFalse();
            await Assert.That(root.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo(root.FieldInfo?.UiName);
        }
    }

    /// <summary>
    /// A cursor that carries the skip slot index while no populator runs has no body behind it: the line must not read the slot,
    /// so it is the label alone on every path (string, characters, UTF-8) and nothing throws.
    /// </summary>
    [Test]
    public async Task ASkipSlotCursorOutsideAPopulatorShowsItsLabelAloneAndNeverThrows()
    {
        (Stack stack, Packet packet, _) = _Parse(snapshotCursors: false, FieldTreeMode.Skip);
        using (stack)
        {
            FieldId portId = stack.GetFieldId("disptest.port")!.Value;
            Field slot = new(packet, FieldBody.SkipStorageIndex, portId);

            PathResults paths = _FormatAllPaths(slot);

            await Assert.That(packet.HasFieldTree).IsFalse();
            await Assert.That(paths.Text).IsEqualTo("Source Port");
            await Assert.That(paths.Chars).IsEqualTo("Source Port");
            await Assert.That(paths.Utf8).IsEqualTo("Source Port");
        }
    }

    /// <summary>
    /// Inside a skip-tree populator both the container and an appended child carry the skip slot index, so both show their label
    /// alone: the line never reads the scratch slot, not even for the container whose text is set.
    /// </summary>
    [Test]
    public async Task InsideASkipTreePopulatorTheContainerAndAChildShowTheirLabelAlone()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        SkipPopulatorProtocol protocol = new();
        ProtocolId id = builder.RegisterProtocol(protocol);
        protocol.RegisterFields(builder, id);
        using Stack stack = builder.Build();
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(protocol.ChildId)]);
        ParseOptions options = new(FieldTreeMode.Skip, cache, firstProtocol: id);
        Frame frame = Frame.Create(
            0, Timestamp.FromSecs(0), new byte[4], LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;

        bool parsed = Packet.TryParse(0, stack, frame, in options, out Packet? packet, out ParseFailure failure);

        await Assert.That(parsed).IsTrue().Because(failure.ToString());
        await Assert.That(packet).IsNotNull();
        await Assert.That(protocol.PopulatorRuns).IsEqualTo(1);
        await Assert.That(protocol.LineInsidePopulator).IsEqualTo("Box");
        await Assert.That(protocol.LineOfAppendedChild).IsEqualTo("Child");
    }

    #endregion

    #region Test doubles

    /// <summary>The formatted results of one cursor, recorded while the dissector built the field.</summary>
    /// <param name="Text"><c>MutField.ToString()</c>.</param>
    /// <param name="CharsFit">Whether the 256-character destination fit.</param>
    /// <param name="Chars">The characters the character path wrote.</param>
    /// <param name="Utf8Fits">Whether the 512-byte destination fit.</param>
    /// <param name="Utf8">The text the UTF-8 path wrote, decoded.</param>
    private readonly record struct CursorLine(string Text, bool CharsFit, string Chars, bool Utf8Fits, string Utf8);

    /// <summary>
    /// Appends one field per line case of the rule (custom text, custom representation, plain value, container, empty text,
    /// non-ASCII text, deferred text, float), plus three fields whose id the stack does not know.
    /// </summary>
    private sealed class LineProtocol : IProtocol
    {
        /// <summary>
        /// Field id values that no stack registers; any valid index works because the packet never checks an id against the stack.
        /// </summary>
        private const int _UnknownBase = 900_000;

        /// <summary>
        /// The count behind <see cref="FactoryCalls"/>; changed with <see cref="Interlocked"/> because a deferred text may be read by
        /// several threads.
        /// </summary>
        private int _FactoryCalls;

        /// <summary>Whether <see cref="Parse"/> formats every cursor it appends and records the results in <see cref="Cursors"/>.</summary>
        public bool SnapshotCursors { get; init; }

        /// <summary>How often the factory of the deferred custom text ran.</summary>
        public int FactoryCalls => Volatile.Read(ref _FactoryCalls);

        /// <summary>The cursor results by field id value (only filled when <see cref="SnapshotCursors"/> is true).</summary>
        public Dictionary<int, CursorLine> Cursors { get; } = [];

        /// <summary>An unregistered field id appended without a value.</summary>
        public FieldId UnknownWithoutValue { get; } = new(_UnknownBase);

        /// <summary>An unregistered field id appended with the value 7.</summary>
        public FieldId UnknownWithValue { get; } = new(_UnknownBase + 1);

        /// <summary>An unregistered field id appended with a custom text.</summary>
        public FieldId UnknownWithText { get; } = new(_UnknownBase + 2);

        // The ids of the registered fields, set by RegisterFields.
        private FieldId _Length;
        private FieldId _Echo;
        private FieldId _Port;
        private FieldId _Container;
        private FieldId _Empty;
        private FieldId _Named;
        private FieldId _Hidden;
        private FieldId _Wide;
        private FieldId _Ratio;
        private FieldId _Deferred;

        /// <inheritdoc/>
        public string Name => "disptest";

        /// <inheritdoc/>
        public string UiName => "Display Test";

        /// <summary>Registers one field per line case of the rule.</summary>
        /// <param name="builder">The stack builder.</param>
        /// <param name="protocolId">The id of this protocol.</param>
        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _Length = builder.RegisterField(protocolId, "disptest.length", "Length", FieldType.U64);
            _Echo = builder.RegisterField(protocolId, "disptest.echo", "UI", FieldType.U64);
            _Port = builder.RegisterField(protocolId, "disptest.port", "Source Port", FieldType.U64);
            _Container = builder.RegisterField(protocolId, "disptest.container", "Container", FieldType.None);
            _Empty = builder.RegisterField(protocolId, "disptest.empty", "Empty Text", FieldType.U64);
            _Named = builder.RegisterField(protocolId, "disptest.named", "Named Container", FieldType.None);
            _Hidden = builder.RegisterField(protocolId, "disptest.hidden", "Hidden Representation", FieldType.None);
            _Wide = builder.RegisterField(protocolId, "disptest.wide", "Size", FieldType.U64);
            _Ratio = builder.RegisterField(protocolId, "disptest.ratio", "Ratio", FieldType.F64);
            _Deferred = builder.RegisterField(protocolId, "disptest.deferred", "Deferred", FieldType.U64);
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            // A custom text replaces the whole line, so the label is part of the text.
            _Record(parentField.AppendWithCustomText(_Length, FieldValue.NewU64(8), new LazyString("Length: 8 bytes")));

            // A custom representation replaces only the value part: the line is label, separator, representation.
            _Record(parentField.Append(_Echo, FieldValue.NewU64(5).WithCustomRepresentation(new LazyString("Echo"))));

            // Neither: label, separator, default text of the data.
            _Record(parentField.Append(_Port, FieldValue.NewU64(53)));

            // No value: the label alone.
            _Record(parentField.Append(_Container, FieldValue.None));

            // A set but empty text still replaces the line: the line is empty.
            _Record(parentField.AppendWithCustomText(_Empty, FieldValue.NewU64(1), new LazyString(string.Empty)));

            // A container with a text shows the text.
            _Record(parentField.AppendWithCustomText(_Named, FieldValue.None, new LazyString("Custom Container")));

            // A representation on a value of type None is not part of the line: there is no value part to replace.
            _Record(parentField.Append(_Hidden, FieldValue.None.WithCustomRepresentation(new LazyString("not shown"))));

            // Non-ASCII text: the UTF-8 path must measure bytes, not characters.
            _Record(parentField.AppendWithCustomText(_Wide, FieldValue.NewU64(1), new LazyString("Größe: ü")));

            // A float: the value text has no fixed length, so the format paths must write exactly the text.
            _Record(parentField.Append(_Ratio, FieldValue.NewF64(0.5)));

            // A deferred text: built on the first read of any path, then shared.
            _Record(parentField.AppendWithCustomText(
                _Deferred,
                FieldValue.NewU64(1),
                LazyString.Lazy(() =>
                {
                    Interlocked.Increment(ref _FactoryCalls);
                    return "Deferred: 42";
                })));

            // Ids the stack does not know have no label: the value alone, nothing, or the custom text.
            _Record(parentField.Append(UnknownWithoutValue, FieldValue.None));
            _Record(parentField.Append(UnknownWithValue, FieldValue.NewU64(7)));
            _Record(parentField.AppendWithCustomText(UnknownWithText, FieldValue.NewU64(7), new LazyString("Own text")));
            return data.Length;
        }

        /// <summary>Formats the cursor with every path and stores the results when snapshots are on.</summary>
        /// <param name="cursor">The cursor returned by the append.</param>
        private void _Record(MutField cursor)
        {
            if (!SnapshotCursors)
            {
                return;
            }

            Span<char> chars = stackalloc char[256];
            bool charsFit = cursor.TryFormat(chars, out int charsWritten, default, CultureInfo.InvariantCulture);
            Span<byte> bytes = stackalloc byte[512];
            bool bytesFit = cursor.TryFormat(bytes, out int bytesWritten, default, CultureInfo.InvariantCulture);
            Cursors[cursor.FieldId.Value] = new CursorLine(
                cursor.ToString(null, CultureInfo.InvariantCulture),
                charsFit,
                new string(chars[..charsWritten]),
                bytesFit,
                Encoding.UTF8.GetString(bytes[..bytesWritten]));
        }
    }

    /// <summary>
    /// A lazy container whose populator reads the line of the container and of an appended child while it runs on the scratch
    /// slot of a skip-tree parse; the value cache that records the child makes the skip parse run the populator.
    /// </summary>
    private sealed class SkipPopulatorProtocol : IProtocol
    {
        /// <summary>The lazy container.</summary>
        public FieldId ContainerId { get; private set; }

        /// <summary>The child that the populator appends; the value cache records it, which makes the skip parse run the populator.</summary>
        public FieldId ChildId { get; private set; }

        /// <summary>How often the populator ran.</summary>
        public int PopulatorRuns { get; private set; }

        /// <summary>The line of the container, read inside the populator.</summary>
        public string? LineInsidePopulator { get; private set; }

        /// <summary>The line of the child cursor, read inside the populator.</summary>
        public string? LineOfAppendedChild { get; private set; }

        /// <inheritdoc/>
        public string Name => "skippop";

        /// <inheritdoc/>
        public string UiName => "Skip Populator";

        /// <summary>Registers the container and the child in one index group (the cache materializes a group).</summary>
        /// <param name="builder">The stack builder.</param>
        /// <param name="protocolId">The id of this protocol.</param>
        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            ContainerId = builder.RegisterFieldInGroup(protocolId, "skippop.box", "Box", FieldType.None, "skippop");
            ChildId = builder.RegisterFieldInGroup(protocolId, "skippop.child", "Child", FieldType.U64, "skippop");
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            _ = parentField.AppendLazyWithCustomText(
                ContainerId,
                FieldValue.None,
                new LazyString("Box: custom"),
                (in MutField container) =>
                {
                    PopulatorRuns++;
                    LineInsidePopulator = container.ToString(null, CultureInfo.InvariantCulture);

                    // The child is appended to a skip packet: it keeps no storage and carries the same slot index as the container,
                    // so the line of each is the label alone.
                    MutField child = container.Append(ChildId, FieldValue.NewU64(3));
                    LineOfAppendedChild = child.ToString(null, CultureInfo.InvariantCulture);
                    return 0;
                });
            return data.Length;
        }
    }

    #endregion
}
