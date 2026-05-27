using System;
using System.Formats.Tar;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GZCTF.Services;
using Xunit;

namespace GZCTF.Test.UnitTests.Services;

/// <summary>
/// Tests for <see cref="AdContainerManager.ReadTarSingleFileAsync"/> — the helper
/// that extracts a single file from the tar archive Docker returns for the AdOps
/// file-inspection view. Locks in the buffering fix: Docker.DotNet's
/// ChunkedReadStream throws <see cref="EndOfStreamException"/> on the read past the
/// final chunk instead of returning 0, which used to abort TarReader mid-entry.
/// </summary>
public class AdTarReadTests
{
    // Build a single-entry tar archive — the shape Docker's GetArchive returns.
    private static byte[] MakeTar(string entryName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(content)
            });
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task ReadTarSingleFileAsync_ReadsRegularFile()
    {
        var content = Encoding.UTF8.GetBytes("hello flag service");
        var b64 = await AdContainerManager.ReadTarSingleFileAsync(
            new MemoryStream(MakeTar("app.py", content)), CancellationToken.None);

        Assert.NotNull(b64);
        Assert.Equal(content, Convert.FromBase64String(b64!));
    }

    [Fact]
    public async Task ReadTarSingleFileAsync_SurvivesEndOfStreamAtTail()
    {
        // The exact Docker.DotNet quirk: the real bytes are delivered, then the
        // read past the end throws. The buffering must swallow it and still parse.
        var content = Encoding.UTF8.GetBytes("result = \"denied\"\n");
        var b64 = await AdContainerManager.ReadTarSingleFileAsync(
            new ThrowAtEndStream(MakeTar("app.py", content)), CancellationToken.None);

        Assert.NotNull(b64);
        Assert.Equal(content, Convert.FromBase64String(b64!));
    }

    [Fact]
    public async Task ReadTarSingleFileAsync_FlagsTruncationOverCap()
    {
        var content = new byte[AdContainerManager.MaxFileBytes + 5000];
        new Random(1).NextBytes(content);

        var b64 = await AdContainerManager.ReadTarSingleFileAsync(
            new MemoryStream(MakeTar("big.bin", content)), CancellationToken.None);
        var decoded = AdContainerManager.DecodeFileOutput(b64);

        Assert.NotNull(decoded);
        Assert.True(decoded!.Value.Truncated);
        Assert.Equal(AdContainerManager.MaxFileBytes, decoded.Value.Data.Length);
    }

    [Fact]
    public async Task ReadTarSingleFileAsync_EmptyArchive_ReturnsNull()
    {
        using var empty = new MemoryStream();
        using (new TarWriter(empty, leaveOpen: true)) { } // archive with no entries
        empty.Position = 0;

        Assert.Null(await AdContainerManager.ReadTarSingleFileAsync(empty, CancellationToken.None));
    }

    [Fact]
    public void DecodeFileOutput_NoFileSentinelOrEmpty_ReturnsNull()
    {
        Assert.Null(AdContainerManager.DecodeFileOutput("__NOFILE__"));
        Assert.Null(AdContainerManager.DecodeFileOutput(""));
        Assert.Null(AdContainerManager.DecodeFileOutput(null));
    }

    /// <summary>Read-only stream over a fixed buffer that throws
    /// <see cref="EndOfStreamException"/> on the read past the end — mimicking
    /// Docker.DotNet's ChunkedReadStream behavior the fix handles.</summary>
    private sealed class ThrowAtEndStream(byte[] data) : Stream
    {
        private int _pos;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length)
                throw new EndOfStreamException("Attempted to read past the end of the stream.");
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos >= data.Length)
                throw new EndOfStreamException("Attempted to read past the end of the stream.");
            var n = Math.Min(buffer.Length, data.Length - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
