// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

/// <summary>
/// Oyun sesini bu bilgisayarda çalan çıkış ("Yayın penceresi" oyun görüntüsü gösterirken; yayın programı sesi bu
/// uygulamadan alır). PS5 oturumunun ses iş parçacığından çağrılır; beklememeli.
/// </summary>
public interface IGameAudioOutput : IDisposable
{
    /// <summary>16 bit PCM, kanallar iç içe. Tampon yalnız çağrı süresince geçerlidir.</summary>
    void Write(ReadOnlySpan<short> pcm, int channels, int rate);

    /// <summary>Bekleyen sesi atar (bağlantı koptu ya da ses kapandı).</summary>
    void Reset();
}

/// <summary>Yayın penceresinde oyun görüntüsünün kare hızı.</summary>
public static class GameVideoRate
{
    public const int Standard = 30;
    public const int Smooth = 60;

    /// <summary>Kayıtlı değer okunamazsa 30 (Discord'un ücretsiz yayını da en çok 30 kare).</summary>
    public static int FromStored(int? stored) => stored == Smooth ? Smooth : Standard;
}
