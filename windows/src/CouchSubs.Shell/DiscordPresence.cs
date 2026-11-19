// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using CouchSubs.Core;

namespace CouchSubs.Shell;

/// <summary>
/// Discord "oynuyor" durumunun içeriği ve Discord IPC iletileri (yerel bağlantının kendisi uygulamada:
/// DiscordIpcPresence). Başlık Discord uygulamasının adıdır; satırlar: "Türkçe altyazıyla" ve yayın penceresi açıksa
/// "Yayında"; görselin üstüne gelince oynanan oyunun adı.
/// </summary>
public static class DiscordPresence
{
    /// <summary>Varsayılan: görsel anahtarı yok; Discord o zaman uygulamanın kendi ikonunu gösterir (Developer Portal ›
    /// General Information › App Icon). Art Assets'e yüklenen bir görselin anahtarı ya da https adresi de verilebilir.</summary>
    public const string DefaultImage = "";

    /// <summary>Uygulama kimliği yalnız rakamdır (17-20 hane); başka her şey atılır.</summary>
    public static string CleanAppId(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length is >= 17 and <= 20 ? digits : "";
    }

    /// <summary>Gösterilecek durum; kimlik geçersizse null (durum gösterilmez).</summary>
    public static DiscordActivity? For(string appId, string image, string? playedGame, bool translating, bool streaming,
                                       DateTimeOffset since, Localizer text, bool xbox = false, bool likeGame = false,
                                       IReadOnlyCollection<string>? assets = null, string? appName = null)
    {
        if (CleanAppId(appId) is not { Length: > 0 } id) return null;
        // Gerçek oyun gibi: yalnız uygulamanın adı ("Grand Theft Auto VI"), görselleri ve süre; CouchSubs, altyazı ya
        // da konsol izi yok (bilgisayarda oynanıyormuş gibi).
        if (likeGame) return LikeGame(id, image, since, assets ?? [], appName);
        var details = translating ? text["With Turkish subtitles"] : text["With CouchSubs"];
        var state = streaming ? text["Streaming"] : null;
        // Köşede konsolun ikonu: Developer Portal › Rich Presence › Art Assets'e "ps5" (ya da "xbox") adıyla yüklenir.
        return new DiscordActivity(id, details, state, string.IsNullOrWhiteSpace(image) ? null : image.Trim(), playedGame, since)
        {
            SmallImage = xbox ? "xbox" : "ps5",
            SmallText = xbox ? "Xbox" : "PlayStation 5",
        };
    }

    /// <summary>Büyük görsel için aranan Art Assets adları (sırayla): oyunun kapak/afiş görseli.</summary>
    public static readonly string[] CoverAssets = ["banner", "cover", "background", "poster"];
    /// <summary>Köşedeki küçük görsel için aranan Art Assets adları: oyunun logosu.</summary>
    public static readonly string[] LogoAssets = ["logo", "icon"];

    /// <summary>
    /// Discord uygulamasının Art Assets'inde kapak ("banner") varsa büyük görsel, logo ("logo") varsa köşedeki küçük
    /// görseldir; üstüne gelince oyunun adı. Kapak görseli yoksa ayarlardaki görsel kullanılır (ayarlara "logo" yazılsa
    /// da kapak varken büyük görsel kapaktır: logo büyük görselde iki kez görünüyordu). Hiçbiri yoksa Discord
    /// uygulamanın ikonunu gösterir.
    /// </summary>
    private static DiscordActivity LikeGame(string id, string image, DateTimeOffset since, IReadOnlyCollection<string> assets, string? appName)
    {
        string? Find(string[] names) => names.FirstOrDefault(name => assets.Contains(name, StringComparer.OrdinalIgnoreCase));
        var cover = Find(CoverAssets) ?? (string.IsNullOrWhiteSpace(image) ? null : image.Trim());
        var logo = Find(LogoAssets);
        if (string.Equals(cover, logo, StringComparison.OrdinalIgnoreCase)) logo = null;
        // Kapak yoksa logo büyük görsel olur (köşede ikinci kez gösterilmez).
        if (cover is null && logo is not null) (cover, logo) = (logo, null);
        return new DiscordActivity(id, null, null, cover, cover is null ? null : appName, since)
        {
            SmallImage = logo,
            SmallText = logo is null ? null : appName,
        };
    }

    /// <summary>İlk ileti (handshake, işlem kodu 0).</summary>
    public static string Handshake(string appId) =>
        new JsonObject { ["v"] = 1, ["client_id"] = appId }.ToJsonString();

    /// <summary>SET_ACTIVITY iletisi (işlem kodu 1); <paramref name="activity"/> null ise durum kaldırılır.</summary>
    public static string SetActivity(DiscordActivity? activity, int processId, string nonce)
    {
        JsonNode? body = null;
        if (activity is not null)
        {
            var assets = new JsonObject();
            if (activity.LargeImage is { Length: > 0 } image) assets["large_image"] = Limit(image, 300);
            if (activity.LargeText is { Length: > 0 } large) assets["large_text"] = Limit(large, 128);
            if (activity.SmallImage is { Length: > 0 } small) assets["small_image"] = Limit(small, 300);
            if (activity.SmallText is { Length: > 0 } smallText) assets["small_text"] = Limit(smallText, 128);
            var node = new JsonObject
            {
                ["type"] = 0, // "Oynuyor"
                ["timestamps"] = new JsonObject { ["start"] = activity.Started.ToUnixTimeSeconds() },
                ["instance"] = false,
            };
            // Discord 2 karakterden kısa satırı reddeder.
            if (activity.Details is { Length: >= 2 } details) node["details"] = Limit(details, 128);
            if (activity.State is { Length: >= 2 } state) node["state"] = Limit(state, 128);
            if (assets.Count > 0) node["assets"] = assets;
            body = node;
        }
        return new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = processId, ["activity"] = body },
            ["nonce"] = nonce,
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}
