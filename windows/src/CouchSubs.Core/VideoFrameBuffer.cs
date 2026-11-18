// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

/// <summary>
/// Oyun görüntüsünün son karesi ("Yayın penceresi"nde oyunu göstermek için). Kareler saniyede 30'a kadar gelir; her
/// biri için yeni dizi ayrılırsa 1080p'de saniyede ~240 MB çöp oluşurdu. Burada iki tampon dönüşümlü kullanılır: yazan
/// (akışın kare iş parçacığı) arka tampona kilitsiz yazar, yalnız takas kilitlidir; okuyan (arayüz) ön tamponu kilit
/// altında kopyalar. Okuyan yavaş kalırsa ara kareler atlanır, yalnız en yenisi gösterilir.
/// </summary>
public sealed class VideoFrameBuffer
{
    private readonly Lock gate = new();
    private byte[] front = [];
    private byte[] back = [];
    private int frontWidth, frontHeight;
    private long version;
    /// <summary>Yazan tek iş parçacığıdır; aynı anda iki yazan gelirse (ör. yeniden bağlanırken eski oturumun son
    /// karesi) biri atlanır.</summary>
    private int writing;

    /// <summary>
    /// Yeni kare yazılınca, yazanın iş parçacığında çağrılır (kısa tutulmalı; arayüz kendi iş parçacığına aktarır).
    /// Ekran yenilemesini beklemek yerine kare gelince çizmek, 60 kare/sn akışta yakın gelen iki kareden birinin
    /// atlanmasını önler.
    /// </summary>
    public event Action? FrameWritten;

    /// <summary>Her yeni karede artar; okuyan değişmemiş kareyi yeniden kopyalamaz.</summary>
    public long Version
    {
        get { lock (gate) return version; }
    }

    /// <summary>Yerel kodun verdiği BGRA tamponunu kopyalar (tampon yalnız çağrı süresince geçerlidir).</summary>
    public unsafe void Write(nint bgra, int width, int height, int stride)
    {
        if (bgra == 0 || width <= 0 || height <= 0 || stride < width * 4) return;
        if (Interlocked.Exchange(ref writing, 1) == 1) return;
        try
        {
            var size = width * height * 4;
            if (back.Length != size) back = new byte[size];
            var source = (byte*)bgra;
            fixed (byte* target = back)
            {
                if (stride == width * 4)
                    Buffer.MemoryCopy(source, target, size, size);
                else
                    for (var y = 0; y < height; y++)
                        Buffer.MemoryCopy(source + ((long)y * stride), target + ((long)y * width * 4), width * 4, width * 4);
            }
            lock (gate)
            {
                (front, back) = (back, front);
                frontWidth = width;
                frontHeight = height;
                version++;
            }
        }
        finally
        {
            Volatile.Write(ref writing, 0);
        }
        FrameWritten?.Invoke();
    }

    /// <summary>
    /// Son kare <paramref name="since"/> sürümünden yeniyse <paramref name="use"/> kilit altında çağrılır (piksel, en,
    /// boy; satır başına en x 4 bayt) ve yeni sürüm döner; yeni kare yoksa null. <paramref name="use"/> kısa tutulmalı:
    /// kilit sürerken yazan takası bekler.
    /// </summary>
    public long? ReadIfNewer(long since, Action<byte[], int, int> use)
    {
        lock (gate)
        {
            if (version == since || front.Length == 0) return null;
            use(front, frontWidth, frontHeight);
            return version;
        }
    }

    /// <summary>Son kare üzerinde kilit altında hesap yapar (ör. yazı rengi); kare yoksa varsayılan döner. Kısa tutulmalı.</summary>
    public T? Read<T>(Func<byte[], int, int, T> use)
    {
        lock (gate) return front.Length == 0 ? default : use(front, frontWidth, frontHeight);
    }

    /// <summary>Son kareyi bırakır (akış durunca pencere eski kareyi göstermesin).</summary>
    public void Clear()
    {
        lock (gate)
        {
            front = [];
            frontWidth = frontHeight = 0;
            version++;
        }
    }

    /// <summary>Gösterilecek kare var mı.</summary>
    public bool HasFrame
    {
        get { lock (gate) return front.Length > 0; }
    }
}
