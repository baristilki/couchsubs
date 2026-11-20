// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

/// <summary>
/// Fork: oyun modunda kareleri eşit aralıkla göstermek için en küçük bekleme. PS5 akışı 60 kare/sn'dir ama ağdan
/// düzensiz gelir: hareketli sahnede kareler büyür, bazıları birkaç ms geç gelir; geldiği anda gösterilince kamera
/// dönerken görüntü takılıyordu. Kaynağın düzenli saati gelen karelerden izlenir (her kare için beklenen geliş anı);
/// kare bu anın biraz sonrasında (<see cref="MarginMs"/>) gösterilir. Pay geç gelen karelere göre hemen büyür, ağ
/// düzgünleşince yavaşça küçülür; en çok <see cref="MaxMarginMs"/>. Ağ düzgünken bekleme neredeyse sıfırdır.
/// <para>
/// Saat kare sayacıyla ilerler (<c>frames</c>: son çağrıdan beri gelen kare sayısı): gösterim beklerken iki kare
/// birden gelince aralık iki katı ölçülüp saat öne kaçıyor, bekleme büyüyüp kare hızı 30'a düşüyordu.
/// </para>
/// </summary>
public sealed class ArrivalSmoother(double maxMarginMs = 12)
{
    private const double NominalInterval = 1000.0 / 60;
    private double interval = NominalInterval;
    /// <summary>Son işlenen kareden sonraki karenin beklenen geliş anı.</summary>
    private double next = double.NaN;
    private double lastArrival = double.NaN;
    private double firstArrival = double.NaN;
    private long countedFrames;
    private double margin;

    /// <summary>Şu anki bekleme payı (ms).</summary>
    public double MarginMs => margin;
    public double MaxMarginMs { get; } = maxMarginMs;
    public double IntervalMs => interval;

    /// <summary>
    /// Son çağrıdan beri <paramref name="frames"/> kare geldi, en yenisi <paramref name="arrivalMs"/>'de: gösterileceği
    /// an (ms; gelişten önce değil, gelişten en çok <see cref="MaxMarginMs"/> sonra).
    /// </summary>
    public double Due(double arrivalMs, int frames = 1)
    {
        frames = Math.Max(1, frames);
        // Aralık uzun bir pencereden ölçülür (ilk kareden bu yana geçen süre / kare sayısı): tek tek aralıklar ağın
        // düzensizliğiyle oynar, kısa ortalama saati kaydırıyordu. 30 kare/sn akış da (ayardan) böyle tanınır.
        if (double.IsNaN(firstArrival)) firstArrival = arrivalMs;
        else
        {
            countedFrames += frames;
            if (countedFrames >= 60)
            {
                var measured = (arrivalMs - firstArrival) / countedFrames;
                if (measured is > 8 and < 40) interval = measured;
            }
        }
        lastArrival = arrivalMs;
        var predicted = next + (interval * (frames - 1));
        // İlk kare ya da uzun boşluk / kayma (kopma, duraklama): saat bu kareye kurulur.
        if (double.IsNaN(predicted) || Math.Abs(arrivalMs - predicted) > interval * 2)
        {
            next = arrivalMs + interval;
            // Uzun boşluk (kopma): aralık ölçümü de baştan.
            firstArrival = arrivalMs;
            countedFrames = 0;
            return arrivalMs;
        }
        var late = arrivalMs - predicted;
        // Geç gelen kare payı hemen büyütür; sonra her karede ~0,03 ms küçülür (saniyede ~2 ms).
        margin = late > margin ? Math.Min(MaxMarginMs, late + 0.5) : Math.Max(0, margin - (0.03 * frames));
        var due = Math.Clamp(predicted + margin, arrivalMs, arrivalMs + MaxMarginMs);
        // Saat kaynağın gerçek hızına yavaşça yaklaşır (erken/geç gelişin küçük bir kısmı kadar).
        next = predicted + interval + (late * 0.02);
        return due;
    }

    public void Reset()
    {
        next = double.NaN;
        lastArrival = double.NaN;
        firstArrival = double.NaN;
        countedFrames = 0;
        margin = 0;
        interval = NominalInterval;
    }
}
