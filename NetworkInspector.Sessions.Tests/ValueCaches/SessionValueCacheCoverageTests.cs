// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using FieldInfo = System.Reflection.FieldInfo;

namespace NetworkInspector.Sessions.Tests;

/// <summary>Exit-path coverage for session value-cache helpers and slot internals.</summary>
internal sealed class SessionValueCacheCoverageTests
{
    #region Helpers

    private static SessionState _GetState(Session session)
    {
        FieldInfo stateField = typeof(Session).GetField(
            "_State",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (SessionState)stateField.GetValue(session)!;
    }

    private static void _SetPrivateInt(object target, string fieldName, int value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(target, value);
    }

    private static ValueCacheSlot _FirstSlot(Session session)
    {
        FieldInfo field = typeof(Session).GetField(
            "_ValueCacheSlots",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object list = field.GetValue(session)!;
        PropertyInfo snapshot = list.GetType().GetProperty(
            "CurrentSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        Array array = (Array)snapshot.GetValue(list)!;
        return (ValueCacheSlot)array.GetValue(0)!;
    }

    private sealed class CoverListener : IValueCacheListener
    {
        public string UiName => "cover";

        public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive)
        {
        }
    }

    #endregion

    #region Tests

    [Test]
    public async Task IngestValueCache_WhenNotConfigured_IsNull()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        await Assert.That(session.IngestValueCache.HasValue).IsFalse();
    }

    [Test]
    public async Task TryAddValueCache_WhitespaceFieldName_ThrowsInvalidFieldName()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        try
        {
            _ = session.TryAddValueCache(
                new CoverListener(),
                new ValueCacheRequest { FieldNames = ["  "] },
                out _,
                out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheInvalidFieldName);
        }
    }

    [Test]
    public async Task TryAddValueCache_UnknownGroup_ThrowsUnknownField()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        try
        {
            _ = session.TryAddValueCache(
                new CoverListener(),
                new ValueCacheRequest { GroupNames = ["no.such.group"] },
                out _,
                out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheUnknownField);
        }
    }

    [Test]
    public async Task TryAddValueCache_WhitespaceGroupName_ThrowsInvalidFieldName()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        try
        {
            _ = session.TryAddValueCache(
                new CoverListener(),
                new ValueCacheRequest { GroupNames = ["  "] },
                out _,
                out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheInvalidFieldName);
        }
    }

    [Test]
    public async Task TryAddValueCache_UdpGroup_Succeeds()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        bool added = session.TryAddValueCache(
            new CoverListener(),
            new ValueCacheRequest { GroupNames = ["udp"] },
            out ValueCacheInfo? info, out _);
        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
    }

    [Test]
    public async Task Ingest_WithoutIndex_RecordsViaParseFrame()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(2);
        using Session session = new(
            stack,
            new SessionOptions
            {
                ValueCache = new ValueCacheRequest { FieldNames = ["udp.srcport"] },
                IndexPackets = false,
            });
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        ReadOnlyValueCache? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        await Assert.That(ingest!.Value.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count)
            .IsEqualTo(2);
        session.Shutdown();
    }

    [Test]
    public async Task AllocateValueCacheId_LastId_ThenExhausted()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        _SetPrivateInt(_GetState(session), "_NextValueCacheId", ArrayIndexIdRange.MaxValue);

        bool added = session.TryAddValueCache(new CoverListener(), new ValueCacheRequest { FieldNames = ["udp.srcport"] }, out ValueCacheInfo? info, out _);
        await Assert.That(added).IsTrue();
        await Assert.That(info!.Id.Value).IsEqualTo(ArrayIndexIdRange.MaxValue);

        try
        {
            _ = session.TryAddValueCache(new CoverListener(), new ValueCacheRequest { FieldNames = ["udp.srcport"] }, out _, out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheIdExhausted);
        }
    }

    [Test]
    public async Task AllocateValueCacheId_Sentinel_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        _SetPrivateInt(_GetState(session), "_NextValueCacheId", int.MinValue);
        try
        {
            _ = session.TryAddValueCache(new CoverListener(), new ValueCacheRequest { FieldNames = ["udp.srcport"] }, out _, out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheIdExhausted);
        }
    }

    [Test]
    public async Task ValueCacheInfo_CacheWithoutWriter_Throws()
    {
        ValueCacheInfo info = new()
        {
            Id = new ValueCacheId(0),
            UiName = "bare",
        };
        try
        {
            _ = info.Cache;
            throw new InvalidOperationException("Expected InvalidOperationException was not thrown.");
        }
        catch (InvalidOperationException ex)
        {
            await Assert.That(ex.Message).Contains("writer");
        }
    }

    [Test]
    public async Task ValueCacheSlot_InternalAccessors_AreReadable()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        session.TryAddValueCache(
            new CoverListener(),
            new ValueCacheRequest { FieldNames = ["udp.srcport"] },
            out _,
            out _);
        ValueCacheSlot slot = _FirstSlot(session);
        _ = slot.Id;
        _ = slot.Writer;
        _ = slot.Request;
        await Assert.That(slot.FillMode).IsEqualTo(ValueCacheFillMode.PullFill);
    }

    [Test]
    public async Task Restart_IngestListener_RebindsNotifyOnlyWriter()
    {
        CoverListener listener = new();
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(2);
        using Session session = new(
            stack,
            new SessionOptions
            {
                ValueCache = new ValueCacheRequest { FieldNames = ["udp.srcport"] },
                ValueCacheListener = listener,
            });
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        ValueCacheSlot slot = _FirstSlot(session);
        session.Restart(registry => TestHarness.CreateStack(registry));
        _ = slot.Writer;
        await Assert.That(session.IngestValueCache.HasValue).IsTrue();
        session.Shutdown();
    }

    /// <summary>
    /// The NotifyOnly listener of the ingest cache attaches to the ingest info and burns no
    /// <see cref="ValueCacheId"/>: the first runtime cache gets the id right after the ingest cache.
    /// </summary>
    [Test]
    public async Task IngestListenerAllocatesNoValueCacheId()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(
            stack,
            new SessionOptions
            {
                ValueCache = new ValueCacheRequest { FieldNames = ["udp.srcport"] },
                ValueCacheListener = new CoverListener(),
            });

        bool added = session.TryAddValueCache(
            new CoverListener(),
            new ValueCacheRequest { FieldNames = ["udp.srcport"] },
            out ValueCacheInfo? runtime,
            out _);
        IReadOnlyList<ValueCacheInfo> caches = session.GetValueCaches();

        await Assert.That(added).IsTrue();
        await Assert.That(caches.Count).IsEqualTo(2);
        await Assert.That(caches[0].Id.Value).IsEqualTo(0);
        await Assert.That(caches[0].Job).IsNotNull();
        await Assert.That(runtime!.Id.Value).IsEqualTo(1);
        session.Shutdown();
    }

    #endregion
}
