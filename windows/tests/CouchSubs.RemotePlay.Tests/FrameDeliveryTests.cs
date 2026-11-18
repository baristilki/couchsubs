// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.RemotePlay.Tests;

public class FrameDeliveryTests
{
    private static unsafe void Offer(FrameDelivery delivery, byte[] pixels, int width, int height, int stride)
    {
        fixed (byte* pointer = pixels) delivery.Offer((nint)pointer, width, height, stride);
    }

    [Fact]
    public async Task FramesAreCopiedAndSkippedWhileHandlerIsBusy()
    {
        var release = new ManualResetEventSlim();
        var received = new List<Frame>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delivery = new FrameDelivery(frame =>
        {
            lock (received) received.Add(frame);
            if (!first.Task.IsCompleted)
            {
                first.SetResult();
                release.Wait();
            }
            else
            {
                second.TrySetResult();
            }
        });

        // 2x2 kare, satır başına 12 bayt (4 bayt dolgu).
        byte[] pixels = [1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0, 9, 10, 11, 12, 13, 14, 15, 16, 0, 0, 0, 0];
        Offer(delivery, pixels, 2, 2, 12);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Offer(delivery, pixels, 2, 2, 12); // işleyici meşgul: atlanır
        release.Set();
        await Task.Delay(100);
        Offer(delivery, pixels, 2, 2, 12);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (received)
        {
            Assert.Equal(2, received.Count);
            Assert.Equal(8, received[0].Stride);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, received[0].Pixels);
        }
    }

    /// <summary>Kareleri verilen anlarda sunar; her kare dağıtılsın diye işleyicinin bitmesini bekler. Dağıtılan sayı.</summary>
    private static async Task<int> Deliver(IEnumerable<long> times)
    {
        var clock = 0L;
        var received = 0;
        using var arrived = new SemaphoreSlim(0);
        using var delivery = new FrameDelivery(_ =>
        {
            Interlocked.Increment(ref received);
            arrived.Release();
        }, maxPerSecond: StreamSession.MaxFps, now: () => Volatile.Read(ref clock));
        byte[] pixels = [1, 2, 3, 4];
        foreach (var time in times)
        {
            Volatile.Write(ref clock, time);
            Offer(delivery, pixels, 1, 1, 4);
            // Kabul edildiyse dağıtılmasını bekle (sonraki kare "meşgul" diye atlanmasın); edilmediyse kısa bekleme yeter.
            await arrived.WaitAsync(TimeSpan.FromMilliseconds(30));
        }
        return Volatile.Read(ref received);
    }

    [Fact]
    public async Task ThirtyFramesPerSecondAreThinnedToTheReadingRate()
    {
        // Oyun görüntüsü açıkken kareler 30/sn (33 ms arayla) gelir; okuma ortalama 20/sn kare görmeli (15 değil).
        var received = await Deliver(Enumerable.Range(0, 90).Select(i => i * 1000L / 30));
        Assert.InRange(received, 58, 63);
    }

    [Fact]
    public async Task JitteryTwentyFramesPerSecondPassUntouched()
    {
        // Oyun görüntüsü kapalıyken 20/sn; ağ titreşimiyle aralıklar 40-60 ms olsa da kare atlanmamalı.
        var time = 0L;
        var times = Enumerable.Range(0, 40).Select(i => time += i % 2 == 0 ? 40 : 60).ToList();
        Assert.Equal(40, await Deliver(times));
    }

    [Fact]
    public void GameVideoTakesEveryFrameThePs5Sends()
    {
        // 0: yerel sınırlayıcı kapalı (titreşimli akışta kare atmasın); okuma yine FrameDelivery'de seyreltilir.
        Assert.Equal(0, StreamSession.NativeFps(gameVideo: true));
        Assert.Equal(StreamSession.MaxFps, StreamSession.NativeFps(gameVideo: false));
    }
}
