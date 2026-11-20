// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

/// <summary>
/// Oyun görüntüsünün kareleri ("Yayın penceresi"nde oyunu göstermek için). Her kare için yeni dizi ayrılırsa 1080p'de
/// saniyede yüzlerce MB çöp oluşurdu: diziler küçük bir havuzdan dönüşümlü kullanılır. Yazan (akışın kare iş parçacığı)
/// havuzdan aldığı diziye kilitsiz yazar; yalnız kuyruk ve havuz işleri kilitlidir.
/// <para>
/// İki okuma yolu vardır: <see cref="ReadIfNewer"/> en yeni kareyi verir (ara kareler atlanır);
/// <see cref="ReadPaced"/> (fork) kareleri küçük bir kuyruktan, akışın kendi aralığıyla eşit arayla verir. Kareler
/// ağdan düzensiz gelir (bir kare 12 ms, sonraki 21 ms sonra); geldiği anda gösterilince ortalama 60 kare/sn olsa da
/// görüntü sıçıyordu. Kuyruk en çok <see cref="MaxPending"/> kare tutar: gecikme bir iki kareyle sınırlı kalır.
/// </para>
/// </summary>
public sealed class VideoFrameBuffer
{
    private sealed record Frame(byte[] Data, int Width, int Height, long Version, double ArrivedMs);

    /// <summary>Gösterilmeyi bekleyen en çok kare; dolunca en eskisi atlanır.</summary>
    public const int MaxPending = 5;

    private readonly Lock gate = new();
    private readonly Queue<Frame> pending = new();
    private readonly Stack<byte[]> pool = new();
    private readonly FramePacer pacer = new();
    private readonly Func<double> clock;
    private Frame? latest;
    private long version;
    /// <summary>Yazan tek iş parçacığıdır; aynı anda iki yazan gelirse (ör. yeniden bağlanırken eski oturumun son
    /// karesi) biri atlanır.</summary>
    private int writing;

    public VideoFrameBuffer() : this(null) { }

    /// <param name="clock">Milisaniye saati (testler için); verilmezse <see cref="System.Diagnostics.Stopwatch"/>.</param>
    public VideoFrameBuffer(Func<double>? clock)
    {
        if (clock is null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            clock = () => watch.Elapsed.TotalMilliseconds;
        }
        this.clock = clock;
    }

    /// <summary>
    /// Yeni kare yazılınca, yazanın iş parçacığında çağrılır (kısa tutulmalı; arayüz kendi iş parçacığına aktarır).
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
            byte[] target;
            lock (gate)
            {
                // Okuyan yetişemiyorsa en eski bekleyen kare atlanır, dizisi yeniden kullanılır.
                while (pending.Count >= MaxPending) Release(pending.Dequeue().Data);
                target = Take(size);
            }
            var source = (byte*)bgra;
            fixed (byte* into = target)
            {
                if (stride == width * 4)
                    Buffer.MemoryCopy(source, into, size, size);
                else
                    for (var y = 0; y < height; y++)
                        Buffer.MemoryCopy(source + ((long)y * stride), into + ((long)y * width * 4), width * 4, width * 4);
            }
            lock (gate)
            {
                var previous = latest;
                var now = clock();
                latest = new Frame(target, width, height, ++version, now);
                pending.Enqueue(latest);
                pacer.Arrived(now);
                if (previous is not null) Release(previous.Data);
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
    /// kilit sürerken yazan bekler.
    /// </summary>
    public long? ReadIfNewer(long since, Action<byte[], int, int> use)
    {
        lock (gate)
        {
            if (latest is not { } frame || frame.Version == since) return null;
            use(frame.Data, frame.Width, frame.Height);
            return frame.Version;
        }
    }

    /// <summary>
    /// Fork: ekran yenilemesinde çağrılır (<paramref name="refreshMs"/>: ekranın yenileme aralığı). Sırası gelen kare
    /// varsa <paramref name="use"/> kilit altında çağrılır ve sürümü döner; yoksa null. Kareler akışın aralığıyla eşit
    /// arayla verilir; kuyruk birikirse eski kareler atlanır (gecikme büyümesin).
    /// </summary>
    public long? ReadPaced(double refreshMs, Action<byte[], int, int> use)
    {
        lock (gate)
        {
            if (pacer.Next(clock(), pending.Count, refreshMs) is not { } drop) return null;
            for (var i = 0; i < drop; i++) Release(pending.Dequeue().Data);
            var frame = pending.Dequeue();
            use(frame.Data, frame.Width, frame.Height);
            Release(frame.Data);
            return frame.Version;
        }
    }

    /// <summary>Son kare üzerinde kilit altında hesap yapar (ör. yazı rengi); kare yoksa varsayılan döner. Kısa tutulmalı.</summary>
    public T? Read<T>(Func<byte[], int, int, T> use)
    {
        lock (gate) return latest is { } frame ? use(frame.Data, frame.Width, frame.Height) : default;
    }

    /// <summary>Kareleri bırakır (akış durunca pencere eski kareyi göstermesin).</summary>
    public void Clear()
    {
        lock (gate)
        {
            latest = null;
            pending.Clear();
            pool.Clear();
            pacer.Reset();
            version++;
        }
    }

    /// <summary>Gösterilecek kare var mı.</summary>
    public bool HasFrame
    {
        get { lock (gate) return latest is not null; }
    }

    /// <summary>Havuzdan uygun boyda dizi (kilit altında); yoksa yenisi.</summary>
    private byte[] Take(int size)
    {
        while (pool.Count > 0)
        {
            var array = pool.Pop();
            if (array.Length == size) return array;
        }
        return new byte[size];
    }

    /// <summary>Dizi artık ne en yeni karede ne kuyruktaysa havuza döner (kilit altında).</summary>
    private void Release(byte[] array)
    {
        if (ReferenceEquals(latest?.Data, array)) return;
        foreach (var frame in pending)
            if (ReferenceEquals(frame.Data, array)) return;
        if (pool.Count < MaxPending + 2) pool.Push(array);
    }
}

/// <summary>
/// Fork: kare gösterme zamanlaması. Akışın kare aralığı gelen karelerden öğrenilir (yavaş ortalama, 60 kare/sn'de
/// ~16,7 ms). Ekran her yenilendiğinde bir önceki gösterimden bu yana bir kare aralığı geçtiyse (yarım yenileme payıyla)
/// kuyruğun başındaki kare gösterilir; gösterim saati kayıp birikmesin diye aralıkla ilerler. Pencere bir an takılıp
/// kareler birikirse (üç ve fazlası) atılmaz, her yenilemede bir tane gösterilerek yetişilir; yalnız
/// <see cref="KeepQueued"/>'dan fazlası atlanır. Eskiden takılmada biriken kareler atılıyor, 60 yerine 54 kare/sn
/// gösteriliyordu.
/// </summary>
public sealed class FramePacer
{
    /// <summary>Gösterimden sonra kuyrukta kalabilecek en çok kare (gecikme sınırı).</summary>
    public const int KeepQueued = 3;
    /// <summary>Bu kadar kare birikince zaman beklenmez, yetişmek için hemen gösterilir.</summary>
    public const int CatchUpAt = 3;

    private double interval = 1000.0 / 60;
    private double lastArrival = -1;
    private double lastShown = -1;

    /// <summary>Öğrenilen kare aralığı (ms).</summary>
    public double IntervalMs => interval;

    public void Arrived(double nowMs)
    {
        if (lastArrival >= 0)
        {
            var gap = nowMs - lastArrival;
            // Kopma ya da duraklama (100 ms üstü) aralığı bozmasın.
            if (gap is > 2 and < 100) interval = (interval * 0.95) + (gap * 0.05);
        }
        lastArrival = nowMs;
    }

    /// <summary>
    /// Şimdi kare gösterilecekse önce atlanacak kare sayısı (0: baştakini göster), gösterilmeyecekse null.
    /// </summary>
    public int? Next(double nowMs, int queued, double refreshMs)
    {
        if (queued == 0) return null;
        if (lastShown >= 0)
        {
            var due = lastShown + interval;
            if (nowMs + (refreshMs / 2) < due && queued < CatchUpAt) return null;
            // Gösterim saati aralıkla ilerler; geride kalınca (uzun boşluk ya da birikme) şimdiye çekilir.
            lastShown = nowMs - due > interval || queued >= CatchUpAt ? nowMs : due;
        }
        else lastShown = nowMs;
        return Math.Max(0, queued - 1 - KeepQueued);
    }

    public void Reset()
    {
        lastArrival = -1;
        lastShown = -1;
        interval = 1000.0 / 60;
    }
}
