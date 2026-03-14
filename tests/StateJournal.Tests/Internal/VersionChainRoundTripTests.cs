using System.Buffers;
using System.Reflection;
using Xunit;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal.Tests;

public class VersionChainRoundTripTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), $"rbf-test-{Guid.NewGuid()}");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { if (File.Exists(path)) { File.Delete(path); } } catch { }
        }
    }

    #region TypedDict round-trip

    [Fact]
    public void TypedDict_Int_Double_SaveLoad_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Arrange: 创建并填充 TypedDict<int, double>
        var dict = Durable.Dict<int, double>();
        dict.Upsert(1, 3.14);
        dict.Upsert(2, 2.718);
        dict.Upsert(42, -0.0);
        dict.Upsert(100, double.MaxValue);

        // Act: Save → Load
        var saveResult = VersionChain.Save(dict, file);
        Assert.True(saveResult.IsSuccess, $"Save failed: {saveResult.Error}");
        SizedPtr ticket = saveResult.Value;

        var loadResult = VersionChain.Load(file, ticket);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");

        // Assert
        var loaded = Assert.IsAssignableFrom<DurableDict<int, double>>(loadResult.Value);
        Assert.Equal(4, loaded.Count);

        Assert.Equal(GetIssue.None, loaded.Get(1, out double v1));
        Assert.Equal(3.14, v1);

        Assert.Equal(GetIssue.None, loaded.Get(2, out double v2));
        Assert.Equal(2.718, v2);

        Assert.Equal(GetIssue.None, loaded.Get(42, out double v3));
        Assert.Equal(-0.0, v3);
        Assert.True(double.IsNegative(v3));

        Assert.Equal(GetIssue.None, loaded.Get(100, out double v4));
        Assert.Equal(double.MaxValue, v4);
    }

    [Fact]
    public void TypedDict_Int_Int_SaveLoad_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var dict = Durable.Dict<int, int>();
        dict.Upsert(10, 100);
        dict.Upsert(20, 200);
        dict.Upsert(30, -300);

        var saveResult = VersionChain.Save(dict, file);
        Assert.True(saveResult.IsSuccess);
        SizedPtr ticket = saveResult.Value;

        var loadResult = VersionChain.Load(file, ticket);
        Assert.True(loadResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableDict<int, int>>(loadResult.Value);
        Assert.Equal(3, loaded.Count);

        Assert.Equal(GetIssue.None, loaded.Get(10, out int v1));
        Assert.Equal(100, v1);

        Assert.Equal(GetIssue.None, loaded.Get(20, out int v2));
        Assert.Equal(200, v2);

        Assert.Equal(GetIssue.None, loaded.Get(30, out int v3));
        Assert.Equal(-300, v3);
    }

    [Fact]
    public void TypedDict_Long_Float_SaveLoad_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var dict = Durable.Dict<long, float>();
        dict.Upsert(long.MinValue, float.NegativeInfinity);
        dict.Upsert(0L, 0.0f);
        dict.Upsert(long.MaxValue, float.PositiveInfinity);

        var saveResult = VersionChain.Save(dict, file);
        Assert.True(saveResult.IsSuccess);

        var loadResult = VersionChain.Load(file, saveResult.Value);
        Assert.True(loadResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableDict<long, float>>(loadResult.Value);
        Assert.Equal(3, loaded.Count);

        Assert.Equal(GetIssue.None, loaded.Get(long.MinValue, out float v1));
        Assert.Equal(float.NegativeInfinity, v1);

        Assert.Equal(GetIssue.None, loaded.Get(0L, out float v2));
        Assert.Equal(0.0f, v2);

        Assert.Equal(GetIssue.None, loaded.Get(long.MaxValue, out float v3));
        Assert.Equal(float.PositiveInfinity, v3);
    }

    #endregion
    #region MixedDict round-trip

    [Fact]
    public void MixedDict_Int_SaveLoad_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var dict = Durable.Dict<int>();
        dict.Upsert(1, 42);
        dict.Upsert(2, 1.5); // 用精确可表示的浮点数，避免 RoundedDouble 压缩引入误差
        dict.Upsert(3, true);
        dict.UpsertExactDouble(4, 3.14);

        var saveResult = VersionChain.Save(dict, file);
        Assert.True(saveResult.IsSuccess, $"Save failed: {saveResult.Error}");

        var loadResult = VersionChain.Load(file, saveResult.Value);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(loadResult.Value);
        Assert.Equal(4, loaded.Count);

        Assert.True(loaded.TryGet<int>(1, out var vi));
        Assert.Equal(42, vi);

        Assert.True(loaded.TryGet<double>(2, out var vd));
        Assert.Equal(1.5, vd);

        Assert.Equal(GetIssue.None, loaded.Get(3, out bool vb));
        Assert.True(vb);

        Assert.True(loaded.TryGet<double>(4, out var ved));
        Assert.Equal(3.14, ved);
    }

    #endregion
    #region Deltify round-trip (Save twice, Load latest)

    [Fact]
    public void TypedDict_SaveTwice_LoadLatest_DeltifyRoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 第一次 Save (rebase)
        var dict = Durable.Dict<int, double>();
        dict.Upsert(1, 1.0);
        dict.Upsert(2, 2.0);

        var save1 = VersionChain.Save(dict, file);
        Assert.True(save1.IsSuccess);

        // 修改并第二次 Save (可能是 deltify)
        dict.Upsert(3, 3.0);
        dict.Remove(1);

        var save2 = VersionChain.Save(dict, file);
        Assert.True(save2.IsSuccess);

        // Load 最新版本
        var loadResult = VersionChain.Load(file, save2.Value);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int, double>>(loadResult.Value);
        Assert.Equal(2, loaded.Count);

        Assert.Equal(GetIssue.None, loaded.Get(2, out double v2));
        Assert.Equal(2.0, v2);

        Assert.Equal(GetIssue.None, loaded.Get(3, out double v3));
        Assert.Equal(3.0, v3);

        // key=1 已被删除
        Assert.Equal(GetIssue.NotFound, loaded.Get(1, out _));
    }

    [Fact]
    public void TypedDict_EmptyDict_SaveLoad_RoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var dict = Durable.Dict<int, int>();
        // 不插入任何数据

        var saveResult = VersionChain.Save(dict, file);
        Assert.True(saveResult.IsSuccess);

        var loadResult = VersionChain.Load(file, saveResult.Value);
        Assert.True(loadResult.IsSuccess);

        var loaded = Assert.IsAssignableFrom<DurableDict<int, int>>(loadResult.Value);
        Assert.Equal(0, loaded.Count);
    }

    [Fact]
    public void MixedDict_SaveTwice_LoadLatest_DeltifyRoundTrip() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var dict = Durable.Dict<int>();
        dict.Upsert(1, 100);
        dict.Upsert(2, 2.5);

        var save1 = VersionChain.Save(dict, file);
        Assert.True(save1.IsSuccess);

        // 修改
        dict.Upsert(3, false);
        dict.Remove(1);

        var save2 = VersionChain.Save(dict, file);
        Assert.True(save2.IsSuccess);

        var loadResult = VersionChain.Load(file, save2.Value);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");

        var loaded = Assert.IsAssignableFrom<DurableDict<int>>(loadResult.Value);
        Assert.Equal(2, loaded.Count);

        Assert.True(loaded.TryGet<double>(2, out var vd));
        Assert.Equal(2.5, vd);

        Assert.Equal(GetIssue.None, loaded.Get(3, out bool vb));
        Assert.False(vb);

        Assert.Equal(GetIssue.NotFound, loaded.Get(1, out int _));
    }

    #endregion
    #region 无变更幂等 Save

    [Fact]
    public void Save_NoChanges_AfterSave_ReturnsLastTicketIdempotent() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var dict = Durable.Dict<int, int>();
        dict.Upsert(1, 10);
        dict.Upsert(2, 20);

        var save1 = VersionChain.Save(dict, file);
        Assert.True(save1.IsSuccess);
        SizedPtr ticket1 = save1.Value;

        // 无变更再次 Save — 幂等返回同一票据，不写新帧
        var save2 = VersionChain.Save(dict, file);
        Assert.True(save2.IsSuccess);
        Assert.Equal(ticket1, save2.Value);

        // Load ticket1 仍可正常工作
        var loadResult = VersionChain.Load(file, ticket1);
        Assert.True(loadResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableDict<int, int>>(loadResult.Value);
        Assert.Equal(2, loaded.Count);
    }

    #endregion
    #region ForceRebase

    [Fact]
    public void ForceRebase_WithChanges_WritesNewRebaseFrame() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var dict = Durable.Dict<int, int>();
        dict.Upsert(1, 10);
        var save1 = VersionChain.Save(dict, file);
        Assert.True(save1.IsSuccess);

        dict.Upsert(2, 20);
        var save2 = VersionChain.Save(dict, file, forceRebase: true);
        Assert.True(save2.IsSuccess);
        Assert.NotEqual(save1.Value, save2.Value);

        var loadResult = VersionChain.Load(file, save2.Value);
        Assert.True(loadResult.IsSuccess);
        var loaded = Assert.IsAssignableFrom<DurableDict<int, int>>(loadResult.Value);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(GetIssue.None, loaded.Get(1, out int v1));
        Assert.Equal(10, v1);
        Assert.Equal(GetIssue.None, loaded.Get(2, out int v2));
        Assert.Equal(20, v2);
    }

    [Fact]
    public void ForceRebase_NoChanges_WritesCompactSnapshot() {
        var originalPath = GetTempFilePath();
        using var originalFile = RbfFile.CreateNew(originalPath);

        var dict = Durable.Dict<int, double>();
        dict.Upsert(10, 1.0);
        dict.Upsert(20, 2.0);
        VersionChain.Save(dict, originalFile);
        dict.Upsert(30, 3.0);
        dict.Remove(10);
        VersionChain.Save(dict, originalFile);

        // 无待写变更 + forceRebase + 新文件 = compact
        var compactPath = GetTempFilePath();
        using var compactFile = RbfFile.CreateNew(compactPath);
        var saveCompact = VersionChain.Save(dict, compactFile, forceRebase: true);
        Assert.True(saveCompact.IsSuccess);

        var loadResult = VersionChain.Load(compactFile, saveCompact.Value);
        Assert.True(loadResult.IsSuccess, $"Load failed: {loadResult.Error}");
        var loaded = Assert.IsAssignableFrom<DurableDict<int, double>>(loadResult.Value);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(GetIssue.NotFound, loaded.Get(10, out _));
        Assert.Equal(GetIssue.None, loaded.Get(20, out double v20));
        Assert.Equal(2.0, v20);
        Assert.Equal(GetIssue.None, loaded.Get(30, out double v30));
        Assert.Equal(3.0, v30);
    }

    #endregion
    #region Load 后 LatestVersionTicket 修复验证

    [Fact]
    public void Load_LatestVersionTicket_PointsToLoadedVersion() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var dict = Durable.Dict<int, int>();
        dict.Upsert(1, 100);
        var ticket = VersionChain.Save(dict, file).Value;

        var loadResult = VersionChain.Load(file, ticket);
        Assert.True(loadResult.IsSuccess);
        var loaded = loadResult.Value!;

        // 修复前 LatestVersionTicket 会是 default(SizedPtr)
        Assert.Equal(ticket, loaded.HeadTicket);
        Assert.True(loaded.IsTracked);

        // 无变更 Save 幂等返回同一 ticket
        var resave = VersionChain.Save(loaded, file);
        Assert.True(resave.IsSuccess);
        Assert.Equal(ticket, resave.Value);
    }

    #endregion
    #region Corruption Guards

    [Fact]
    public void Load_FrameHasTrailingBytes_ReturnsCorruptionError() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        byte[] typeCode = TypedDictFactory<int, int>.TypeCode!.ToArray();
        byte[] payload = BuildPayload(
            writer => {
                writer.WriteBytes(typeCode); // rebase frame
                writer.BareUInt64(0, false); // previousVersion
                writer.BareUInt32(1, false); // cumulativeCost
                writer.WriteCount(0); // remove count
                writer.WriteCount(0); // upsert count
                writer.BareByte(0x7F, false); // trailing garbage
            }
        );

        uint tag = new FrameTag(UsageKind.Blank, DurableObjectKind.TypedDict, VersionKind.Rebase).Bits;
        var append = file.Append(tag, payload);
        Assert.True(append.IsSuccess);

        var load = VersionChain.Load(file, append.Value);
        Assert.True(load.IsFailure);
        Assert.IsType<SjCorruptionError>(load.Error);
    }

    [Fact]
    public void Load_CyclicVersionChain_ReturnsCorruptionError() {
        var ticketA = SizedPtr.Create(4, 4);
        var ticketB = SizedPtr.Create(8, 4);
        uint deltaTag = new FrameTag(UsageKind.Blank, DurableObjectKind.TypedDict, VersionKind.Delta).Bits;

        byte[] payloadA = BuildPayload(
            writer => {
                writer.WriteBytes(default); // delta frame: empty typeCode
                writer.BareUInt64(ticketB.Serialize(), false);
            }
        );
        byte[] payloadB = BuildPayload(
            writer => {
                writer.WriteBytes(default); // delta frame: empty typeCode
                writer.BareUInt64(ticketA.Serialize(), false);
            }
        );

        using var file = new StubRbfFile(
            new Dictionary<SizedPtr, byte[]> {
                [ticketA] = payloadA,
                [ticketB] = payloadB,
            }, deltaTag
        );

        var load = VersionChain.Load(file, ticketA);
        Assert.True(load.IsFailure);
        var err = Assert.IsType<SjCorruptionError>(load.Error);
        Assert.Contains("cycle", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ExpectObjectMismatch_ReturnsCorruptionError() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var dict = Durable.Dict<int, int>();
        dict.Upsert(1, 10);
        var save = VersionChain.Save(dict, file);
        Assert.True(save.IsSuccess);

        var load = VersionChain.Load(file, save.Value, expectObject: DurableObjectKind.MixedDict);
        Assert.True(load.IsFailure);
        var err = Assert.IsType<SjCorruptionError>(load.Error);
        Assert.Contains("Unexpected ObjectKind", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ExpectUsageMismatch_ReturnsCorruptionError() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var dict = Durable.Dict<int, int>();
        dict.Upsert(1, 10);
        var save = VersionChain.Save(dict, file);
        Assert.True(save.IsSuccess);

        var load = VersionChain.Load(file, save.Value, expectUsage: UsageKind.UserPayload);
        Assert.True(load.IsFailure);
        var err = Assert.IsType<SjCorruptionError>(load.Error);
        Assert.Contains("Unexpected UsageKind", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_DeltaTagWithNonEmptyTypeCode_ReturnsCorruptionError() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        byte[] typeCode = TypedDictFactory<int, int>.TypeCode!.ToArray();
        byte[] payload = BuildPayload(
            writer => {
                writer.WriteBytes(typeCode); // non-empty typeCode
                writer.BareUInt64(0, false);
                writer.BareUInt32(1, false);
                writer.WriteCount(0);
                writer.WriteCount(0);
            }
        );
        uint tag = new FrameTag(UsageKind.Blank, DurableObjectKind.TypedDict, VersionKind.Delta).Bits;
        var append = file.Append(tag, payload);
        Assert.True(append.IsSuccess);

        var load = VersionChain.Load(file, append.Value);
        Assert.True(load.IsFailure);
        var err = Assert.IsType<SjCorruptionError>(load.Error);
        Assert.Contains("VersionKind mismatch", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    private delegate void WriteDiffAction(BinaryDiffWriter writer);
    private static byte[] BuildPayload(WriteDiffAction writeAction) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writeAction(writer);
        return buffer.WrittenSpan.ToArray();
    }

    private sealed class StubRbfFile(Dictionary<SizedPtr, byte[]> payloadByTicket, uint tag = 0) : IRbfFile {
        public long TailOffset => 0;

        public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) => throw new NotSupportedException();
        public RbfFrameBuilder BeginAppend() => throw new NotSupportedException();
        public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer) => throw new NotSupportedException();
        public RbfReverseSequence ScanReverse(bool showTombstone = false) => throw new NotSupportedException();
        public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => throw new NotSupportedException();
        public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => throw new NotSupportedException();
        public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => throw new NotSupportedException();
        public void DurableFlush() => throw new NotSupportedException();
        public void Truncate(long newLengthBytes) => throw new NotSupportedException();
        public void SetupReadLog(string? logPath) => throw new NotSupportedException();

        public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr) {
            if (!payloadByTicket.TryGetValue(ptr, out byte[]? payload)) { return new StubError("STUB.NotFound", $"No frame mapped for ticket {ptr}."); }
            return CreatePooledFrame(ptr, payload, tag);
        }

        public void Dispose() {
        }

        private static RbfPooledFrame CreatePooledFrame(SizedPtr ticket, byte[] payload, uint tag) {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buffer, 0);
            var ctor = typeof(RbfPooledFrame).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] {
                    typeof(byte[]), typeof(SizedPtr), typeof(uint),
                    typeof(int), typeof(int), typeof(int), typeof(bool),
                },
                null
            ) ?? throw new InvalidOperationException("RbfPooledFrame internal ctor not found.");

            return (RbfPooledFrame)ctor.Invoke(
                new object[] {
                    buffer,                     ticket,                     tag,                     0,                     payload.Length,                     0,                     false,
            }
            );
        }
    }

    private sealed record StubError(string ErrorCode, string Message)
        : AteliaError(ErrorCode, Message);
}
