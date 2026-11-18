// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class VideoFrameBufferTests
{
    private static void Write(VideoFrameBuffer buffer, byte[] pixels, int width, int height, int stride)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { buffer.Write(handle.AddrOfPinnedObject(), width, height, stride); }
        finally { handle.Free(); }
    }

    private static (byte[] Pixels, int Width, int Height)? Read(VideoFrameBuffer buffer, ref long version)
    {
        (byte[], int, int)? result = null;
        if (buffer.ReadIfNewer(version, (pixels, width, height) => result = ((byte[])pixels.Clone(), width, height)) is { } newer)
            version = newer;
        return result;
    }

    [Fact]
    public void EmptyBufferHasNothingToRead()
    {
        var buffer = new VideoFrameBuffer();
        var version = 0L;
        Assert.False(buffer.HasFrame);
        Assert.Null(Read(buffer, ref version));
    }

    [Fact]
    public void PaddedRowsAreCopiedTightly()
    {
        var buffer = new VideoFrameBuffer();
        // 2x2 kare, satır başına 12 bayt (4 bayt dolgu).
        byte[] pixels = [1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0, 9, 10, 11, 12, 13, 14, 15, 16, 0, 0, 0, 0];
        Write(buffer, pixels, 2, 2, 12);
        var version = 0L;
        var frame = Read(buffer, ref version);
        Assert.NotNull(frame);
        Assert.Equal(2, frame.Value.Width);
        Assert.Equal(2, frame.Value.Height);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, frame.Value.Pixels);
    }

    [Fact]
    public void UnchangedFrameIsNotReadTwiceAndLatestFrameWins()
    {
        var buffer = new VideoFrameBuffer();
        var version = 0L;
        Write(buffer, [1, 1, 1, 1], 1, 1, 4);
        Assert.NotNull(Read(buffer, ref version));
        Assert.Null(Read(buffer, ref version));

        // Okuyan yavaş kaldı: aradaki kare atlanır, yalnız en yenisi okunur.
        Write(buffer, [2, 2, 2, 2], 1, 1, 4);
        Write(buffer, [3, 3, 3, 3], 1, 1, 4);
        Assert.Equal(new byte[] { 3, 3, 3, 3 }, Read(buffer, ref version)!.Value.Pixels);
    }

    [Fact]
    public void SizeChangeAndClearAreHandled()
    {
        var buffer = new VideoFrameBuffer();
        var version = 0L;
        Write(buffer, [1, 1, 1, 1], 1, 1, 4);
        Write(buffer, new byte[2 * 1 * 4], 2, 1, 8);
        Assert.Equal(2, Read(buffer, ref version)!.Value.Width);

        buffer.Clear();
        Assert.False(buffer.HasFrame);
        Assert.Null(Read(buffer, ref version));

        Write(buffer, [4, 4, 4, 4], 1, 1, 4);
        Assert.Equal(new byte[] { 4, 4, 4, 4 }, Read(buffer, ref version)!.Value.Pixels);
    }

    [Fact]
    public void EveryWrittenFrameIsAnnounced()
    {
        var buffer = new VideoFrameBuffer();
        var announced = 0;
        buffer.FrameWritten += () => announced++;
        Write(buffer, [1, 1, 1, 1], 1, 1, 4);
        Write(buffer, [2, 2, 2, 2], 1, 1, 4);
        Write(buffer, [1, 1, 1, 1], 0, 1, 4); // geçersiz kare duyurulmaz
        Assert.Equal(2, announced);
    }

    [Fact]
    public void InvalidFramesAreIgnored()
    {
        var buffer = new VideoFrameBuffer();
        buffer.Write(0, 2, 2, 8);
        Write(buffer, [1, 1, 1, 1], 1, 1, 2); // satır, piksellerden kısa
        Write(buffer, [1, 1, 1, 1], 0, 1, 4);
        Assert.False(buffer.HasFrame);
    }

    [Fact]
    public async Task ConcurrentWritesAndReadsStayConsistent()
    {
        var buffer = new VideoFrameBuffer();
        const int width = 64, height = 36;
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var pixels = new byte[width * height * 4];
            for (byte value = 0; !stop.IsCancellationRequested; value++)
            {
                Array.Fill(pixels, value);
                Write(buffer, pixels, width, height, width * 4);
            }
        });
        var version = 0L;
        var reads = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < deadline)
        {
            if (Read(buffer, ref version) is not { } frame) continue;
            reads++;
            // Okunan kare yarım yazılmış olmamalı: tüm baytlar aynı karenin değeri.
            Assert.All(frame.Pixels, b => Assert.Equal(frame.Pixels[0], b));
        }
        stop.Cancel();
        await writer;
        Assert.True(reads > 0);
    }
}
