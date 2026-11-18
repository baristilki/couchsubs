// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using CouchSubs.RemotePlay;
using CouchSubs.Shell;

namespace CouchSubs.App.Services;

/// <summary>
/// Discord masaüstü uygulamasının yerel bağlantısı (named pipe <c>\\.\pipe\discord-ipc-N</c>): "oynuyor" durumunu
/// (Rich Presence) ayarlar. İleti: 4 bayt işlem kodu, 4 bayt uzunluk (küçük uçlu), UTF-8 JSON. Kendi iş
/// parçacığında çalışır; Discord kapalıysa ya da bağlantı koparsa 15 sn'de bir yeniden dener, uygulamayı bekletmez.
/// Uygulama kimliği değişince yeniden el sıkışılır.
/// </summary>
internal sealed class DiscordIpcPresence : IDiscordPresence
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(15);

    private readonly object gate = new();
    private readonly Thread thread;
    private DiscordActivity? wanted;
    private bool changed;
    private bool disposed;

    public DiscordIpcPresence()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "CouchSubs Discord" };
        thread.Start();
    }

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Uygulamanın herkese açık bilgisi (<c>/applications/{id}/rpc</c>; anahtar gerekmez): adı.</summary>
    public async Task<string?> AppNameAsync(string appId)
    {
        try
        {
            using var response = await Http.GetAsync($"https://discord.com/api/v10/applications/{Uri.EscapeDataString(appId)}/rpc").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return json.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            RemotePlayLog.Note($"Discord uygulama adı alınamadı: {error.Message}");
            return null;
        }
    }

    public void Update(DiscordActivity? activity)
    {
        lock (gate)
        {
            wanted = activity;
            changed = true;
            Monitor.Pulse(gate);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Monitor.Pulse(gate);
        }
        // Durum kaldırılsın diye kısa süre beklenir (kapanırken Discord'da "oynuyor" asılı kalmasın).
        thread.Join(TimeSpan.FromSeconds(1));
    }

    private void Run()
    {
        NamedPipeClientStream? pipe = null;
        string? connectedId = null;
        while (true)
        {
            DiscordActivity? activity;
            bool quit;
            lock (gate)
            {
                // Bağlıyken yalnız değişiklik beklenir; bağlı değilken ve gösterilecek durum varken yeniden denenir.
                while (!changed && !disposed && (pipe is not null || wanted is null)) Monitor.Wait(gate);
                if (!changed && !disposed && pipe is null) Monitor.Wait(gate, Retry);
                activity = wanted;
                changed = false;
                quit = disposed;
            }
            try
            {
                if (quit)
                {
                    if (pipe is not null) Send(pipe, 1, DiscordPresence.SetActivity(null, Environment.ProcessId, Guid.NewGuid().ToString()));
                    pipe?.Dispose();
                    return;
                }
                // Kimlik değiştiyse ya da durum kalktıysa eski bağlantı kapatılır.
                if (pipe is not null && (activity is null || activity.AppId != connectedId))
                {
                    if (activity is null) Send(pipe, 1, DiscordPresence.SetActivity(null, Environment.ProcessId, Guid.NewGuid().ToString()));
                    pipe.Dispose();
                    pipe = null;
                    connectedId = null;
                }
                if (activity is null) continue;
                if (pipe is null)
                {
                    pipe = Connect();
                    if (pipe is null)
                    {
                        lock (gate) changed = false;
                        continue;
                    }
                    Send(pipe, 0, DiscordPresence.Handshake(activity.AppId));
                    Read(pipe); // READY (ya da hata: kimlik geçersizse Discord bağlantıyı kapatır)
                    connectedId = activity.AppId;
                    RemotePlayLog.Note("Discord durumu bağlandı");
                }
                Send(pipe, 1, DiscordPresence.SetActivity(activity, Environment.ProcessId, Guid.NewGuid().ToString()));
                var reply = Read(pipe);
                if (reply.Contains("\"evt\":\"ERROR\"", StringComparison.Ordinal))
                    RemotePlayLog.Note($"Discord durumu reddedildi: {Shorten(reply)}");
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
            {
                RemotePlayLog.Note($"Discord bağlantısı koptu: {error.Message}");
                pipe?.Dispose();
                pipe = null;
                connectedId = null;
                // Durum korunur; bir sonraki denemede yeniden gönderilir.
                lock (gate) changed = wanted is not null;
            }
        }
    }

    /// <summary>Discord'un açtığı ilk boru (0-9); Discord kapalıysa null.</summary>
    private static NamedPipeClientStream? Connect()
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.None);
            try
            {
                pipe.Connect(200);
                return pipe;
            }
            catch (Exception error) when (error is TimeoutException or IOException)
            {
                pipe.Dispose();
            }
        }
        return null;
    }

    private static void Send(Stream pipe, int opcode, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        pipe.Write(frame);
        pipe.Flush();
    }

    private static string Read(Stream pipe)
    {
        Span<byte> header = stackalloc byte[8];
        pipe.ReadExactly(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (length is < 0 or > 1 << 20) throw new IOException("Discord: bozuk ileti");
        var payload = new byte[length];
        pipe.ReadExactly(payload);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        // 2: Discord bağlantıyı kapattı (ör. geçersiz uygulama kimliği).
        if (opcode == 2) throw new IOException($"Discord kapattı: {Shorten(Encoding.UTF8.GetString(payload))}");
        return Encoding.UTF8.GetString(payload);
    }

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200];
}
