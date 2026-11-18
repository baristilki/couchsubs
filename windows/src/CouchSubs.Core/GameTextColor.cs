// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

/// <summary>
/// Oyunun kendi altyazısının yazı rengi ("Yayın penceresi"ndeki çeviri aynı renkte görünsün diye). Oyun altyazıları
/// hemen hep koyu kenarlı ya da gölgeli açık renk yazıdır: altyazı bölgesinde yakınında koyu piksel olan parlak
/// pikseller yazı sayılır (gökyüzü gibi parlak zemin yazı sanılmaz). Konuşan adı ayrı renkte olabilir; yazı
/// piksellerinin en kalabalık renk grubu seçilir, yani cümlenin rengi. Renksize yakınsa beyaz döner.
/// </summary>
public static class GameTextColor
{
    public readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>Çevirinin varsayılan beyazı (StreamWindow'daki kırık beyazla aynı).</summary>
    public static readonly Rgb White = new(0xF7, 0xF7, 0xF5);

    /// <summary>Bu kadar yazı pikseli bulunmazsa (bölgede altyazı yok) renk okunmaz.</summary>
    internal const int MinTextPixels = 40;

    /// <summary>
    /// BGRA karede (satır başına en x 4 bayt) <paramref name="region"/> içindeki yazının rengi; yazı bulunamazsa null.
    /// Hızlı olsun diye bölge en çok ~320 sütun ve ~90 satır örneklenir.
    /// </summary>
    public static Rgb? Sample(byte[] bgra, int width, int height, CropRegion region) =>
        SampleColors(bgra, width, height, region)?.Text;

    /// <summary>Cümlenin rengi ve (ayrı renkteyse) baştaki konuşan adının rengi.</summary>
    public readonly record struct Colors(Rgb Text, Rgb? Speaker);

    /// <summary>
    /// Yazının renkleri: en kalabalık renk grubu cümledir. Ondan belirgin biçimde farklı, yeterince büyük ve cümleden
    /// solda duran ikinci bir grup varsa o konuşan adıdır ("Spider-Man:" mavi, cümle beyaz gibi).
    /// </summary>
    public static Colors? SampleColors(byte[] bgra, int width, int height, CropRegion region)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;
        int left = Math.Clamp((int)(width * region.Left), 0, width - 1), right = Math.Clamp((int)(width * region.Right), left + 1, width);
        int top = Math.Clamp((int)(height * region.Top), 0, height - 1), bottom = Math.Clamp((int)(height * region.Bottom), top + 1, height);
        var stepX = Math.Max(1, (right - left) / 320);
        var stepY = Math.Max(1, (bottom - top) / 90);
        // Kenar/gölge araması: yazı kalınlığının biraz ötesi (1080p'de ~3 px).
        var reach = Math.Max(2, height / 360);
        // Renk grupları: kanal başına 4 seviye (64 grup); her grubun renk toplamı, sayısı ve yatay konum toplamı.
        var sums = new long[64 * 3];
        var counts = new int[64];
        var xs = new long[64];
        var total = 0;
        for (var y = top + reach; y < bottom - reach; y += stepY)
        {
            for (var x = left + reach; x < right - reach; x += stepX)
            {
                var i = ((y * width) + x) * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                if (!Bright(r, g, b)) continue;
                if (!DarkNear(bgra, width, x, y, reach)) continue;
                var bucket = ((r >> 6) << 4) | ((g >> 6) << 2) | (b >> 6);
                counts[bucket]++;
                sums[bucket * 3] += r;
                sums[(bucket * 3) + 1] += g;
                sums[(bucket * 3) + 2] += b;
                xs[bucket] += x;
                total++;
            }
        }
        if (total < MinTextPixels) return null;
        var first = 0;
        for (var k = 1; k < counts.Length; k++)
            if (counts[k] > counts[first]) first = k;
        // Ondan belirgin biçimde farklı renkteki en büyük ikinci grup (en az %6).
        var second = -1;
        for (var k = 0; k < counts.Length; k++)
        {
            if (k == first || counts[k] < Math.Max(MinTextPixels / 4, total * 0.06)) continue;
            if (Close(Average(sums, counts, k), Average(sums, counts, first))) continue;
            if (second < 0 || counts[k] > counts[second]) second = k;
        }
        if (second < 0) return new Colors(Average(sums, counts, first), null);
        // İki renk varsa soldaki konuşan adı, sağdaki cümledir; hangisinin büyük olduğu önemli değil ("Spider-Man: Oh."
        // satırında ad cümleden uzun, en kalabalık renk adın rengiydi ve cümle turuncu sanılıyordu).
        var firstX = (double)xs[first] / counts[first];
        var secondX = (double)xs[second] / counts[second];
        var (speaker, text) = firstX < secondX ? (first, second) : (second, first);
        return new Colors(Average(sums, counts, text), Average(sums, counts, speaker));
    }

    private static Rgb Average(long[] sums, int[] counts, int bucket)
    {
        var n = counts[bucket];
        return Normalize((int)(sums[bucket * 3] / n), (int)(sums[(bucket * 3) + 1] / n), (int)(sums[(bucket * 3) + 2] / n));
    }

    /// <summary>Yazı pikseli olabilecek kadar açık: beyaz/açık yazı ya da doygun renkli yazı (mavi bir ad beyazdan koyudur).</summary>
    private static bool Bright(int r, int g, int b)
    {
        if (Luma(r, g, b) >= 150) return true;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return max >= 160 && max - min >= 60;
    }

    internal static bool Close(Rgb a, Rgb b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 60;

    /// <summary>Renksize yakınsa beyaz; renkliyse parlaklığı tama çekilir (gölgeyle karışıp kararmış ortalama düzelsin).</summary>
    internal static Rgb Normalize(int r, int g, int b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max - min < 40 || max == 0) return White;
        var scale = 255.0 / max;
        return new Rgb((byte)Math.Round(r * scale), (byte)Math.Round(g * scale), (byte)Math.Round(b * scale));
    }

    private static int Luma(int r, int g, int b) => ((r * 3) + (g * 6) + b) / 10;

    private static bool DarkNear(byte[] bgra, int width, int x, int y, int reach)
    {
        ReadOnlySpan<(int Dx, int Dy)> around = [(reach, 0), (-reach, 0), (0, reach), (0, -reach)];
        foreach (var (dx, dy) in around)
        {
            var i = (((y + dy) * width) + x + dx) * 4;
            if (Luma(bgra[i + 2], bgra[i + 1], bgra[i]) < 80) return true;
        }
        return false;
    }

    /// <summary>
    /// Örneklerden kararlı renk: tek bir karedeki sapma (patlama, parlak efekt) rengi değiştirmesin diye yeni renk
    /// art arda <see cref="Confirmations"/> örnekte görülünce geçilir.
    /// </summary>
    public sealed class Tracker
    {
        public const int Confirmations = 3;
        private Rgb? candidate;
        private int seen;

        public Rgb Current { get; private set; } = White;

        /// <summary>En az bir renk doğrulandı mı (konuşan adı için: doğrulanmadıysa ad cümlenin renginde yazılır).</summary>
        public bool Confirmed { get; private set; }

        /// <summary>Yeni örnek (null: bölgede yazı yok, renk korunur). Renk değiştiyse true.</summary>
        public bool Offer(Rgb? sample)
        {
            if (sample is not { } color) return false;
            if (Close(color, Current))
            {
                candidate = null;
                seen = 0;
                return false;
            }
            if (candidate is { } waiting && Close(color, waiting)) seen++;
            else
            {
                candidate = color;
                seen = 1;
            }
            if (seen < Confirmations) return false;
            Current = color;
            Confirmed = true;
            candidate = null;
            seen = 0;
            return true;
        }

        public void Reset()
        {
            Current = White;
            Confirmed = false;
            candidate = null;
            seen = 0;
        }
    }
}
