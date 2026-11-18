// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using CouchSubs.Core;
using CouchSubs.RemotePlay;

namespace CouchSubs.App.Services;

/// <summary>
/// PS5'ten gelen oyun sesini Windows'un kendi ses çıkışıyla (winmm waveOut) çalar; ek kitaplık gerekmez. Discord ve
/// OBS "uygulama sesi" olarak bu süreci yakalar. Ses parçaları 20 ms'lik tamponlara toplanır; kuyrukta en çok
/// <see cref="BufferCount"/> tampon (~160 ms) tutulur: ağ takılırsa birikip gecikme büyümesin diye fazlası atılır.
/// </summary>
internal sealed unsafe partial class WaveOutGameAudio : IGameAudioOutput
{
    private const int BufferCount = 8;
    private const int BufferMs = 20;
    private const uint WhdrDone = 0x1;
    private const uint WhdrInQueue = 0x10;
    private const int WaveMapper = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [LibraryImport("winmm.dll")]
    private static partial int waveOutOpen(out nint handle, int device, WaveFormatEx* format, nint callback, nint instance, uint flags);
    [LibraryImport("winmm.dll")]
    private static partial int waveOutPrepareHeader(nint handle, WaveHdr* header, uint size);
    [LibraryImport("winmm.dll")]
    private static partial int waveOutUnprepareHeader(nint handle, WaveHdr* header, uint size);
    [LibraryImport("winmm.dll")]
    private static partial int waveOutWrite(nint handle, WaveHdr* header, uint size);
    [LibraryImport("winmm.dll")]
    private static partial int waveOutReset(nint handle);
    [LibraryImport("winmm.dll")]
    private static partial int waveOutClose(nint handle);
    [LibraryImport("winmm.dll")]
    private static partial int waveOutPause(nint handle);
    [LibraryImport("winmm.dll")]
    private static partial int waveOutRestart(nint handle);

    /// <summary>Çalmaya başlamadan önce kuyrukta beklenen tampon (60 ms): ağdan düzensiz gelen ses kesik çalmasın.</summary>
    private const int Prebuffer = 3;
    private bool paused;
    private int queuedWhilePaused;

    private readonly Lock gate = new();
    private nint device;
    private int channels, rate;
    private WaveHdr* headers;
    private byte* data;
    private int bufferBytes;
    /// <summary>Doldurulan tampon ve içindeki bayt.</summary>
    private int filling = -1, filled;
    private bool disposed;
    private long dropped;

    public void Write(ReadOnlySpan<short> pcm, int channels, int rate)
    {
        lock (gate)
        {
            if (disposed) return;
            if (device == 0 || channels != this.channels || rate != this.rate)
            {
                Close();
                if (!Open(channels, rate)) return;
            }
            var bytes = MemoryMarshal.AsBytes(pcm);
            while (bytes.Length > 0)
            {
                if (filling < 0 && (filling = FreeBuffer()) < 0)
                {
                    // Kuyruk dolu: çalıcı geride kaldı; bu parça atılır (gecikme büyümez).
                    if (++dropped % 50 == 1) RemotePlayLog.Note($"oyun sesi: kuyruk dolu, {dropped} parça atıldı");
                    return;
                }
                var take = Math.Min(bytes.Length, bufferBytes - filled);
                bytes[..take].CopyTo(new Span<byte>(data + ((long)filling * bufferBytes) + filled, take));
                filled += take;
                bytes = bytes[take..];
                if (filled < bufferBytes) continue;
                // Kuyruk tamamen boşaldıysa çalıcı aç kaldı: yeniden biraz biriktirip öyle sürdür.
                if (!paused && QueuedCount() == 0)
                {
                    waveOutPause(device);
                    paused = true;
                    queuedWhilePaused = 0;
                }
                var header = &headers[filling];
                header->BufferLength = (uint)bufferBytes;
                header->Flags &= ~WhdrDone;
                waveOutWrite(device, header, (uint)sizeof(WaveHdr));
                filling = -1;
                filled = 0;
                if (paused && ++queuedWhilePaused >= Prebuffer)
                {
                    waveOutRestart(device);
                    paused = false;
                }
            }
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            if (device != 0)
            {
                waveOutReset(device);
                waveOutPause(device);
                paused = true;
                queuedWhilePaused = 0;
            }
            filling = -1;
            filled = 0;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Close();
        }
    }

    private int QueuedCount()
    {
        var count = 0;
        for (var i = 0; i < BufferCount; i++)
            if ((headers[i].Flags & WhdrInQueue) != 0) count++;
        return count;
    }

    /// <summary>Kuyrukta olmayan (çalınmış ya da hiç kullanılmamış) tampon; yoksa -1.</summary>
    private int FreeBuffer()
    {
        for (var i = 0; i < BufferCount; i++)
            if ((headers[i].Flags & WhdrInQueue) == 0) return i;
        return -1;
    }

    private bool Open(int channels, int rate)
    {
        var format = new WaveFormatEx
        {
            FormatTag = 1, // PCM
            Channels = (ushort)channels,
            SamplesPerSec = (uint)rate,
            BitsPerSample = 16,
            BlockAlign = (ushort)(channels * 2),
            AvgBytesPerSec = (uint)(rate * channels * 2),
        };
        if (waveOutOpen(out var handle, WaveMapper, &format, 0, 0, 0) != 0 || handle == 0)
        {
            RemotePlayLog.Note($"oyun sesi: ses çıkışı açılamadı ({channels} kanal, {rate} Hz)");
            return false;
        }
        device = handle;
        this.channels = channels;
        this.rate = rate;
        bufferBytes = rate * BufferMs / 1000 * channels * 2;
        headers = (WaveHdr*)NativeMemory.AllocZeroed((nuint)(BufferCount * sizeof(WaveHdr)));
        data = (byte*)NativeMemory.AllocZeroed((nuint)(BufferCount * bufferBytes));
        for (var i = 0; i < BufferCount; i++)
        {
            headers[i].Data = (nint)(data + ((long)i * bufferBytes));
            headers[i].BufferLength = (uint)bufferBytes;
            waveOutPrepareHeader(device, &headers[i], (uint)sizeof(WaveHdr));
        }
        filling = -1;
        filled = 0;
        waveOutPause(device);
        paused = true;
        queuedWhilePaused = 0;
        RemotePlayLog.Note($"oyun sesi çalınıyor: {channels} kanal, {rate} Hz");
        return true;
    }

    private void Close()
    {
        if (device != 0)
        {
            waveOutReset(device);
            for (var i = 0; i < BufferCount; i++) waveOutUnprepareHeader(device, &headers[i], (uint)sizeof(WaveHdr));
            waveOutClose(device);
            device = 0;
        }
        if (headers != null) NativeMemory.Free(headers);
        if (data != null) NativeMemory.Free(data);
        headers = null;
        data = null;
        channels = rate = 0;
    }
}
