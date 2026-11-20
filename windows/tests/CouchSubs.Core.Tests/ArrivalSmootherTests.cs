// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

/// <summary>Fork: oyun modunda düzensiz gelen kareler eşit aralıkla gösterilir.</summary>
public class ArrivalSmootherTests
{
    [Fact]
    public void JitteryArrivalsAreShownEvenly()
    {
        var smoother = new ArrivalSmoother();
        var random = new Random(4);
        var shown = new List<double>();
        for (var i = 0; i < 600; i++)
        {
            // Kareler 0-9 ms geç gelir (hareketli sahne), ara sıra 12 ms.
            var late = random.NextDouble() < 0.1 ? 12 : random.NextDouble() * 9;
            shown.Add(smoother.Due((i * 1000.0 / 60) + late));
        }
        var gaps = shown.Skip(120).Zip(shown.Skip(121), (a, b) => b - a).ToList();
        // Geliş aralıkları 4-29 ms arasında oynarken gösterim aralıkları kareye yakın kalır.
        Assert.True(gaps.Count(gap => Math.Abs(gap - 16.67) > 2) < gaps.Count / 20,
            string.Join(", ", gaps.Where(gap => Math.Abs(gap - 16.67) > 2).Take(10).Select(gap => gap.ToString("0.0"))));
        Assert.InRange(smoother.MarginMs, 4, 12.5);
    }

    [Fact]
    public void SteadyArrivalsAreNotDelayed()
    {
        var smoother = new ArrivalSmoother();
        double delay = 0;
        for (var i = 0; i < 300; i++)
        {
            var arrival = i * 1000.0 / 60;
            delay = smoother.Due(arrival) - arrival;
        }
        Assert.InRange(delay, 0, 0.6);
    }

    /// <summary>Gösterim beklerken iki kare birden gelse de saat öne kaçmaz, bekleme büyümez (eskiden 30 kareye düşüyordu).</summary>
    [Fact]
    public void SkippedWakeUpsDoNotDriftTheClock()
    {
        var smoother = new ArrivalSmoother();
        var random = new Random(5);
        double worst = 0;
        for (var i = 0; i < 600; )
        {
            var frames = random.NextDouble() < 0.3 ? 2 : 1;
            i += frames;
            var arrival = (i * 1000.0 / 60) + (random.NextDouble() * 3);
            var wait = smoother.Due(arrival, frames) - arrival;
            if (i > 120) worst = Math.Max(worst, wait);
        }
        Assert.InRange(worst, 0, 6);
    }

    [Fact]
    public void LongGapResetsTheClock()
    {
        var smoother = new ArrivalSmoother();
        for (var i = 0; i < 60; i++) smoother.Due(i * 1000.0 / 60);
        Assert.InRange(smoother.Due(5000), 5000, 5001);
    }
}
