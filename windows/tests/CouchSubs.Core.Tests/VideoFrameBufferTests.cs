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

/// <summary>Fork: kareler düzensiz gelse de eşit aralıkla gösterilir.</summary>
public class FramePacerTests
{
    [Fact]
    public void JitteryArrivalsAreShownEvenly()
    {
        var pacer = new FramePacer();
        const double refresh = 1000.0 / 240;
        // 60 kare/sn, ama kareler 12 ve 21 ms arayla geliyor.
        double arrive = 0, now = 0;
        var queued = 0;
        var shownAt = new List<double>();
        for (var i = 0; i < 120; i++)
        {
            pacer.Arrived(arrive);
            queued++;
            var next = arrive + (i % 2 == 0 ? 12 : 21.3);
            for (; now < next; now += refresh)
                if (pacer.Next(now, queued, refresh) is { } drop)
                {
                    queued -= drop + 1;
                    shownAt.Add(now);
                }
            arrive = next;
        }
        var gaps = shownAt.Skip(20).Zip(shownAt.Skip(21), (a, b) => b - a).ToList();
        // Gösterim aralıkları yaklaşık bir kare (16,7 ms ± bir yenileme); gelişteki 12/21 ms sıçrama kalmaz.
        Assert.All(gaps, gap => Assert.InRange(gap, 16.7 - refresh - 0.1, 16.7 + refresh + 0.1));
    }

    /// <summary>Pencere ara sıra takılsa da (20-30 ms) kareler atılmaz.</summary>
    [Fact]
    public void StallsDoNotDropFrames()
    {
        var random = new Random(2);
        var arrivals = Enumerable.Range(0, 600).Select(i => (i * 1000.0 / 60) + (random.NextDouble() * 8) - 4).Order().ToList();
        var pacer = new FramePacer();
        int queued = 0, shown = 0, dropped = 0, next = 0;
        for (var now = 0.0; now < (600 * 17) + 200; now += random.NextDouble() < 0.75 ? 8.3 : 16 + (random.NextDouble() * 14))
        {
            while (next < arrivals.Count && arrivals[next] <= now) { queued++; next++; }
            while (queued > VideoFrameBuffer.MaxPending) { queued--; dropped++; }
            if (pacer.Next(now, queued, 11.4) is not { } drop) continue;
            dropped += drop;
            queued -= drop + 1;
            shown++;
        }
        Assert.True(shown >= 590, $"{shown} gösterildi, {dropped} atıldı");
    }

    [Fact]
    public void BacklogIsDroppedToKeepLatencyLow()
    {
        var pacer = new FramePacer();
        Assert.Equal(0, pacer.Next(0, 1, 4));
        Assert.Null(pacer.Next(5, 1, 4));
        // Birikme yetişmek için hemen gösterilir; yalnız sınırın üstü atılır.
        Assert.Equal(0, pacer.Next(19, 3, 4));
        Assert.Equal(1, pacer.Next(20, 5, 4));
        Assert.Null(pacer.Next(21, 0, 4));
    }

    /// <summary>Fork: düşük gecikmede kuyruk beklenmez, en yeni kare hemen gösterilir.</summary>
    [Fact]
    public void LowLatencyShowsTheNewestFrameAtOnce()
    {
        var now = 0.0;
        var buffer = new VideoFrameBuffer(() => now);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(new byte[] { 1, 1, 1, 1 }, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { for (var i = 0; i < 3; i++) buffer.Write(handle.AddrOfPinnedObject(), 1, 1, 4); }
        finally { handle.Free(); }
        now = 1;
        Assert.Equal(3, buffer.ReadPaced(4, (_, _, _) => { }, lowLatency: true));
        Assert.Null(buffer.ReadPaced(4, (_, _, _) => { }, lowLatency: true));
        Assert.Equal(1, buffer.LastWaitMs);
    }

    [Fact]
    public void PacedReadsGiveOneFramePerInterval()
    {
        var now = 0.0;
        var buffer = new VideoFrameBuffer(() => now);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(new byte[] { 1, 1, 1, 1 }, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            buffer.Write(handle.AddrOfPinnedObject(), 1, 1, 4);
            now = 2;
            buffer.Write(handle.AddrOfPinnedObject(), 1, 1, 4);
        }
        finally { handle.Free(); }
        Assert.Equal(1, buffer.ReadPaced(4, (_, _, _) => { }));
        now = 6;
        Assert.Null(buffer.ReadPaced(4, (_, _, _) => { }));
        now = 17;
        Assert.Equal(2, buffer.ReadPaced(4, (_, _, _) => { }));
        Assert.True(buffer.HasFrame);
    }
}
