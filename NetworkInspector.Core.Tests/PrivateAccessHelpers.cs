// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Test-only accessors for private members that take <see cref="Span{T}"/> parameters
/// and cannot be invoked through standard reflection.
/// </summary>
internal static class Lz4CodecPrivateAccess
{
    private delegate int EmitSequenceDelegate(
        Span<byte> output,
        int outputPos,
        ReadOnlySpan<byte> input,
        int literalStart,
        int literalLength,
        int offset,
        int matchLength);

    private delegate int EmitLastLiteralsDelegate(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int outputPos,
        int anchor,
        int inputEnd);

    private static readonly EmitSequenceDelegate _EmitSequence = _CreateEmitSequence();
    private static readonly EmitLastLiteralsDelegate _EmitLastLiterals = _CreateEmitLastLiterals();

    public static int EmitSequence(
        Span<byte> output,
        int outputPos,
        ReadOnlySpan<byte> input,
        int literalStart,
        int literalLength,
        int offset,
        int matchLength)
        => _EmitSequence(output, outputPos, input, literalStart, literalLength, offset, matchLength);

    public static int EmitLastLiterals(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int outputPos,
        int anchor,
        int inputEnd)
        => _EmitLastLiterals(input, output, outputPos, anchor, inputEnd);

    private static EmitSequenceDelegate _CreateEmitSequence()
    {
        MethodInfo method = typeof(Lz4Codec).GetMethod(
            "_EmitSequence", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (EmitSequenceDelegate)Delegate.CreateDelegate(typeof(EmitSequenceDelegate), method);
    }

    private static EmitLastLiteralsDelegate _CreateEmitLastLiterals()
    {
        MethodInfo method = typeof(Lz4Codec).GetMethod(
            "_EmitLastLiterals", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (EmitLastLiteralsDelegate)Delegate.CreateDelegate(typeof(EmitLastLiteralsDelegate), method);
    }

    private delegate bool ReadVarLenDelegate(
        ReadOnlySpan<byte> input,
        ref int inputPos,
        ref int accumulated);

    public static bool ReadVarLen(ReadOnlySpan<byte> input, ref int inputPos, ref int accumulated)
    {
        MethodInfo method = typeof(Lz4Codec).GetMethod(
            "_ReadVarLen", BindingFlags.NonPublic | BindingFlags.Static)!;
        ReadVarLenDelegate del = (ReadVarLenDelegate)Delegate.CreateDelegate(typeof(ReadVarLenDelegate), method);
        return del(input, ref inputPos, ref accumulated);
    }
}

internal static class Xoroshiro128PlusPlusPrivateAccess
{
    private delegate void FillBytesVector128Delegate(
        Span<byte> buffer,
        ulong s0B,
        ulong s1B,
        ulong s0C,
        ulong s1C,
        ulong s0D,
        ulong s1D);

    private delegate void FillBytesScalar4Delegate(
        Span<byte> buffer,
        ulong s0B,
        ulong s1B,
        ulong s0C,
        ulong s1C,
        ulong s0D,
        ulong s1D);

    public static void FillBytesVector128(
        Xoroshiro128PlusPlus rng,
        Span<byte> buffer,
        ulong s0B,
        ulong s1B,
        ulong s0C,
        ulong s1C,
        ulong s0D,
        ulong s1D)
    {
        MethodInfo method = typeof(Xoroshiro128PlusPlus).GetMethod(
            "_FillBytesVector128", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FillBytesVector128Delegate del = (FillBytesVector128Delegate)Delegate.CreateDelegate(
            typeof(FillBytesVector128Delegate), rng, method);
        del(buffer, s0B, s1B, s0C, s1C, s0D, s1D);
    }

    public static void FillBytesScalar4(
        Xoroshiro128PlusPlus rng,
        Span<byte> buffer,
        ulong s0B,
        ulong s1B,
        ulong s0C,
        ulong s1C,
        ulong s0D,
        ulong s1D)
    {
        MethodInfo method = typeof(Xoroshiro128PlusPlus).GetMethod(
            "_FillBytesScalar4", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FillBytesScalar4Delegate del = (FillBytesScalar4Delegate)Delegate.CreateDelegate(
            typeof(FillBytesScalar4Delegate), rng, method);
        del(buffer, s0B, s1B, s0C, s1C, s0D, s1D);
    }
}
