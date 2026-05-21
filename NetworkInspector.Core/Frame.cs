// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Represents a single captured network frame with metadata and raw data.
/// Comparable by <see cref="FrameId"/>.
/// <para>
/// Frames are created exclusively via the <see cref="Create"/> factory method, which
/// validates that a valid <see cref="FrameInterfaceId"/> is registered in the
/// <see cref="FrameInterfaceRegistry"/>. The registry reference is mandatory and enables
/// <see cref="Packet"/> to validate at construction time that the frame and the protocol
/// stack share the same registry (a single reference-equality check on the hot path).
/// </para>
/// <para>
/// <b>Default value:</b> <c>default(Frame)</c> is the <see cref="Invalid"/> sentinel.
/// Reading <see cref="Registry"/>, <see cref="Length"/>, <see cref="IsEmpty"/>,
/// <see cref="Data"/>, <see cref="LinkType"/>, <see cref="InterfaceId"/>, <see cref="Id"/>,
/// <see cref="Timestamp"/> or <see cref="HasInterface"/> on a default-initialised frame
/// throws <see cref="InvalidOperationException"/>. Use <see cref="IsValid"/> to test for
/// the sentinel before access.
/// </para>
/// <para>
/// <b>Thread-safety:</b> <see cref="Frame"/> is an immutable value type — every field is
/// <see langword="readonly"/> and the wrapped <see cref="ReadOnlyMemory{T}"/> guarantees
/// read-only access to the payload. Instances are safe to share across any number of threads
/// without synchronization, provided the underlying byte memory is not mutated externally
/// after the frame is published.
/// </para>
/// </summary>
public readonly struct Frame : IComparable<Frame>, IEquatable<Frame>
{
    private readonly FrameId _Id;
    private readonly Timestamp _Timestamp;
    private readonly ReadOnlyMemory<byte> _Data;
    private readonly LinkType _LinkType;
    private readonly FrameInterfaceId _InterfaceId;
    private readonly FrameInterfaceRegistry _Registry;

    /// <summary>Creates a new frame (private — use <see cref="Create"/> factory).</summary>
    private Frame(
        FrameId id,
        Timestamp timestamp,
        ReadOnlyMemory<byte> data,
        LinkType linkType,
        FrameInterfaceId interfaceId,
        FrameInterfaceRegistry registry)
    {
        _Id = id;
        _Timestamp = timestamp;
        _Data = data;
        _LinkType = linkType;
        _InterfaceId = interfaceId;
        _Registry = registry;
    }

    #region Factory

    /// <summary>
    /// Creates a new frame, validating that a valid interface ID is registered.
    /// </summary>
    /// <param name="id">Unique frame identifier.</param>
    /// <param name="timestamp">Capture timestamp.</param>
    /// <param name="data">Raw frame data (zero-copy view).</param>
    /// <param name="linkType">Link-layer header type.</param>
    /// <param name="interfaceId">
    /// Interface that captured this frame. When <see cref="FrameInterfaceId.IsValid"/> is
    /// <see langword="true"/>, the ID must be registered in <paramref name="registry"/>.
    /// <see cref="FrameInterfaceId.Invalid"/> is allowed without registry validation.
    /// </param>
    /// <param name="registry">
    /// The <see cref="FrameInterfaceRegistry"/> that owns <paramref name="interfaceId"/>.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A <see cref="ParseResult{T}"/> containing the new frame on success,
    /// or a <see cref="ParseError"/> if <paramref name="interfaceId"/> is valid but not
    /// registered.
    /// </returns>
    public static ParseResult<Frame> Create(
        FrameId id,
        Timestamp timestamp,
        ReadOnlyMemory<byte> data,
        LinkType linkType,
        FrameInterfaceId interfaceId,
        FrameInterfaceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // Validate: a valid interface ID must exist in the registry
        if (interfaceId.IsValid && registry.Get(interfaceId) is null)
        {
            return ParseError.Custom("frame",
                $"Interface ID {interfaceId} is not registered in the FrameInterfaceRegistry");
        }

        return new Frame(id, timestamp, data, linkType, interfaceId, registry);
    }

    #endregion

    #region Properties

    /// <summary>
    /// Whether this frame is a valid, non-default instance produced via <see cref="Create"/>.
    /// Returns <see langword="false"/> for <c>default(Frame)</c> / <see cref="Invalid"/>.
    /// </summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Registry is not null;
    }

    /// <summary>The default-initialised frame sentinel. <see cref="IsValid"/> is <see langword="false"/>.</summary>
    public static Frame Invalid => default;

    /// <summary>Unique frame identifier.</summary>
    /// <exception cref="InvalidOperationException">The frame is the default sentinel.</exception>
    public FrameId Id
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfInvalid();
            return _Id;
        }
    }

    /// <summary>Capture timestamp.</summary>
    /// <exception cref="InvalidOperationException">The frame is the default sentinel.</exception>
    public Timestamp Timestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfInvalid();
            return _Timestamp;
        }
    }

    /// <summary>Raw frame data (zero-copy view).</summary>
    /// <exception cref="InvalidOperationException">The frame is the default sentinel.</exception>
    public ReadOnlyMemory<byte> Data
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfInvalid();
            return _Data;
        }
    }

    /// <summary>Link-layer header type.</summary>
    /// <exception cref="InvalidOperationException">The frame is the default sentinel.</exception>
    public LinkType LinkType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfInvalid();
            return _LinkType;
        }
    }

    /// <summary>Interface that captured this frame.</summary>
    /// <exception cref="InvalidOperationException">The frame is the default sentinel.</exception>
    public FrameInterfaceId InterfaceId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfInvalid();
            return _InterfaceId;
        }
    }

    /// <summary>Whether the frame has a valid interface assignment.</summary>
    /// <exception cref="InvalidOperationException">The frame is the default sentinel.</exception>
    public bool HasInterface
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfInvalid();
            return _InterfaceId.IsValid;
        }
    }

    /// <summary>
    /// The <see cref="FrameInterfaceRegistry"/> that owns <see cref="InterfaceId"/>.
    /// Always non-null for frames created via <see cref="Create"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The frame is the default sentinel.</exception>
    public FrameInterfaceRegistry Registry
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfInvalid();
            return _Registry;
        }
    }

    /// <summary>Whether the frame data is empty.</summary>
    /// <exception cref="InvalidOperationException">The frame is the default sentinel.</exception>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfInvalid();
            return _Data.IsEmpty;
        }
    }

    /// <summary>Length of the frame data in bytes.</summary>
    /// <exception cref="InvalidOperationException">The frame is the default sentinel.</exception>
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfInvalid();
            return _Data.Length;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfInvalid()
    {
        if (_Registry is null)
        {
            ThrowDefault();
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDefault() =>
        throw new InvalidOperationException("Frame is the default sentinel; create frames via Frame.Create.");

    #endregion

    #region Comparison

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Frame other) => _Id.CompareTo(other._Id);

    /// <summary>Compares frames by <see cref="FrameId"/>.</summary>
    public static bool operator <(Frame left, Frame right) => left._Id < right._Id;
    /// <summary>Compares frames by <see cref="FrameId"/>.</summary>
    public static bool operator >(Frame left, Frame right) => left._Id > right._Id;
    /// <summary>Compares frames by <see cref="FrameId"/>.</summary>
    public static bool operator <=(Frame left, Frame right) => left._Id <= right._Id;
    /// <summary>Compares frames by <see cref="FrameId"/>.</summary>
    public static bool operator >=(Frame left, Frame right) => left._Id >= right._Id;
    /// <summary>Checks equality by <see cref="FrameId"/>.</summary>
    public static bool operator ==(Frame left, Frame right) => left._Id == right._Id;
    /// <summary>Checks inequality by <see cref="FrameId"/>.</summary>
    public static bool operator !=(Frame left, Frame right) => left._Id != right._Id;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Frame other) => _Id == other._Id;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Frame other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _Id.GetHashCode();
    #endregion
}
