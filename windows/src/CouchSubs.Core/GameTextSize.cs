// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

/// <summary>
/// Oyunun kendi altyazısının yazı boyu ("Yayın penceresi"ndeki çeviri aynı boyda görünsün diye). Okunan İngilizce
/// altyazının kutusu (tüm satırların birleşimi) bilinir; satır sayısı kutunun yüksekliğinden tahmin edilir
/// (oyun altyazılarında bir satır 1080p'de ~40-50 px, karenin ~%4'ü), satır yüksekliği buradan çıkar. Tek bir hatalı
/// okuma boyu zıplatmasın diye son örneklerin ortancası kullanılır.
/// </summary>
public sealed class GameTextSize
{
    /// <summary>Tipik bir altyazı satırının kutu yüksekliği (kareye oranla); satır sayısı tahmini için.</summary>
    internal const double TypicalLine = 0.042;
    private const int Window = 5;
    private readonly Queue<double> recent = new();

    /// <summary>Kutudaki tek satırın yüksekliği (kareye oranla); kutu geçersizse null.</summary>
    public static double? LineHeight(TextBox box)
    {
        var height = box.Bottom - box.Top;
        if (height <= 0.005 || height > 0.4) return null;
        var lines = Math.Clamp((int)Math.Round(height / TypicalLine), 1, 4);
        return height / lines;
    }

    /// <summary>Yeni kutu; satır yüksekliği geçerliyse örneklere eklenir.</summary>
    public void Offer(TextBox box)
    {
        if (LineHeight(box) is not { } line) return;
        recent.Enqueue(line);
        while (recent.Count > Window) recent.Dequeue();
    }

    /// <summary>Son örneklerin ortancası (kareye oranla satır yüksekliği); örnek yoksa null.</summary>
    public double? Current
    {
        get
        {
            if (recent.Count == 0) return null;
            var sorted = recent.Order().ToList();
            return sorted[sorted.Count / 2];
        }
    }

    public void Reset() => recent.Clear();
}
