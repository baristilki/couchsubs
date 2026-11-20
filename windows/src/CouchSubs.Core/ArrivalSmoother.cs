// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

/// <summary>
/// Fork: oyun modunda kareleri eşit aralıkla göstermek için en küçük bekleme. PS5 akışı 60 kare/sn'dir ama ağdan
/// düzensiz gelir: hareketli sahnede kareler büyür, bazıları birkaç ms geç gelir; geldiği anda gösterilince kamera
/// dönerken görüntü takılıyordu. Kaynağın düzenli saati gelen karelerden izlenir (her kare için beklenen geliş anı);
/// kare bu anın biraz sonrasında (<see cref="MarginMs"/>) gösterilir. Pay geç gelen karelere göre hemen büyür, ağ
/// düzgünleşince yavaşça küçülür; en çok <see cref="MaxMarginMs"/>. Ağ düzgünken bekleme neredeyse sıfırdır.
/// </summary>
public sealed class ArrivalSmoother(double maxMarginMs = 12)
{
    private double interval = 1000.0 / 60;
    private double expected = double.NaN;
    private double lastArrival = double.NaN;
    private double margin;

    /// <summary>Şu anki bekleme payı (ms).</summary>
    public double MarginMs => margin;
    public double MaxMarginMs { get; } = maxMarginMs;
    public double IntervalMs => interval;

    /// <summary><paramref name="arrivalMs"/>'de gelen karenin gösterileceği an (ms; gelişten önce olmaz).</summary>
    public double Due(double arrivalMs)
    {
        if (!double.IsNaN(lastArrival))
        {
            var gap = arrivalMs - lastArrival;
            if (gap is > 2 and < 100) interval = (interval * 0.98) + (gap * 0.02);
        }
        lastArrival = arrivalMs;
        // İlk kare ya da uzun boşluk (kopma, duraklama): saat bu kareye kurulur.
        if (double.IsNaN(expected) || Math.Abs(arrivalMs - expected) > interval * 2)
        {
            expected = arrivalMs + interval;
            return arrivalMs + margin;
        }
        var late = arrivalMs - expected;
        // Geç gelen kare payı hemen büyütür; sonra her karede ~0,03 ms küçülür (saniyede ~2 ms).
        margin = late > margin ? Math.Min(MaxMarginMs, late + 0.5) : Math.Max(0, margin - 0.03);
        var due = Math.Max(arrivalMs, expected + margin);
        // Saat kaynağın gerçek hızına yavaşça yaklaşır (erken/geç gelişin küçük bir kısmı kadar).
        expected += interval + (late * 0.02);
        return due;
    }

    public void Reset()
    {
        expected = double.NaN;
        lastArrival = double.NaN;
        margin = 0;
    }
}
