// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using CouchSubs.Core;

namespace CouchSubs.Shell.Tests;

internal sealed class FakeDiscord : IDiscordPresence
{
    public List<DiscordActivity?> Updates { get; } = [];
    public void Update(DiscordActivity? activity) => Updates.Add(activity);
    public string? Name { get; set; } = "Grand Theft Auto VI";
    public Task<string?> AppNameAsync(string appId) => Task.FromResult(Name);
    public IReadOnlyList<string>? Assets { get; set; }
    public Task<IReadOnlyList<string>?> AppAssetsAsync(string appId) => Task.FromResult(Assets);
    public void Dispose() { }
}

public class DiscordPresenceTests
{
    private static readonly Localizer Tr = Localizer.ForLanguage("tr");
    private const string Id = "1540309526903787582";

    [Fact]
    public void AppIdKeepsOnlyAValidSnowflake()
    {
        Assert.Equal(Id, DiscordPresence.CleanAppId($" {Id} "));
        Assert.Equal("", DiscordPresence.CleanAppId("12345"));
        Assert.Equal("", DiscordPresence.CleanAppId(null));
    }

    [Fact]
    public void ActivityShowsSubtitlesStreamingAndTheGame()
    {
        var since = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var activity = DiscordPresence.For(Id, "", "Grand Theft Auto VI", translating: true, streaming: true, since, Tr)!;
        Assert.Equal("Türkçe altyazıyla", activity.Details);
        Assert.Equal("Yayında", activity.State);
        Assert.Null(activity.LargeImage); // görsel yoksa Discord uygulamanın ikonunu gösterir
        Assert.Equal("Grand Theft Auto VI", activity.LargeText);
        Assert.Null(DiscordPresence.For("geçersiz", "", null, false, false, since, Tr));

        using var json = JsonDocument.Parse(DiscordPresence.SetActivity(activity, 42, "n1"));
        var root = json.RootElement;
        Assert.Equal("SET_ACTIVITY", root.GetProperty("cmd").GetString());
        var sent = root.GetProperty("args").GetProperty("activity");
        Assert.Equal(42, root.GetProperty("args").GetProperty("pid").GetInt32());
        Assert.Equal("Türkçe altyazıyla", sent.GetProperty("details").GetString());
        Assert.Equal(1_800_000_000, sent.GetProperty("timestamps").GetProperty("start").GetInt64());
        Assert.False(sent.GetProperty("assets").TryGetProperty("large_image", out _));
        // Köşede konsolun ikonu ("ps5" görsel anahtarı) ve adı.
        Assert.Equal("ps5", sent.GetProperty("assets").GetProperty("small_image").GetString());
        Assert.Equal("PlayStation 5", sent.GetProperty("assets").GetProperty("small_text").GetString());

        using var cleared = JsonDocument.Parse(DiscordPresence.SetActivity(null, 42, "n2"));
        Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("args").GetProperty("activity").ValueKind);
    }

    [Fact]
    public void ControllerShowsAndClearsTheStatus()
    {
        var discord = new FakeDiscord();
        var services = new FakeServices { Discord = discord };
        var controller = AppControllerTests.Make(services);
        Assert.All(discord.Updates, update => Assert.Null(update));

        controller.DiscordAppId = Id;
        controller.DiscordLikeGame = false;
        controller.DiscordPresenceEnabled = true;
        var shown = discord.Updates[^1];
        Assert.NotNull(shown);
        Assert.Equal(Id, shown.AppId);
        Assert.Null(shown.State);
        // Yayın penceresinin başlığı Discord uygulamasının adından gelir ("Yayında: Grand Theft Auto VI").
        Assert.Equal("Grand Theft Auto VI", controller.DiscordAppName);

        // Yayın penceresi açılınca "Yayında".
        controller.GameVideoWanted = true;
        Assert.NotNull(discord.Updates[^1]!.State);

        controller.DiscordPresenceEnabled = false;
        Assert.Null(discord.Updates[^1]);
        Assert.Null(controller.DiscordAppName);
        controller.Dispose();
    }

    /// <summary>Uygulamada "banner" ve "logo" görselleri varsa: büyük görsel kapak, köşede logo, üstlerinde oyunun adı.</summary>
    [Fact]
    public void LikeGameUsesTheAppsBannerAndLogo()
    {
        var discord = new FakeDiscord { Assets = ["logo", "banner"] };
        var controller = AppControllerTests.Make(new FakeServices { Discord = discord });
        controller.DiscordAppId = Id;
        controller.DiscordPresenceEnabled = true;
        var shown = discord.Updates[^1]!;
        Assert.Equal("banner", shown.LargeImage);
        Assert.Equal("logo", shown.SmallImage);
        Assert.Equal("Grand Theft Auto VI", shown.LargeText);
        Assert.Null(shown.Details);
        controller.Dispose();
    }

    /// <summary>Ayarlardaki görsele "logo" yazılmış olsa da kapak varken büyük görsel kapaktır.</summary>
    [Fact]
    public void LikeGamePrefersTheBannerOverTheImageSetting()
    {
        var activity = DiscordPresence.For(Id, "logo", null, true, true, DateTimeOffset.UnixEpoch, Tr, likeGame: true, assets: ["logo", "banner"], appName: "GTA")!;
        Assert.Equal("banner", activity.LargeImage);
        Assert.Equal("logo", activity.SmallImage);
    }

    [Fact]
    public void LikeGameWithOnlyALogoShowsItLarge()
    {
        var activity = DiscordPresence.For(Id, "", null, true, true, DateTimeOffset.UnixEpoch, Tr, likeGame: true, assets: ["logo"], appName: "GTA")!;
        Assert.Equal("logo", activity.LargeImage);
        Assert.Null(activity.SmallImage);
    }

    /// <summary>Gerçek oyun gibi (varsayılan): yalnız oyunun adı; CouchSubs, altyazı, "Yayında" ve konsol ikonu yok.</summary>
    [Fact]
    public void LikeGameShowsOnlyTheGame()
    {
        var discord = new FakeDiscord();
        var controller = AppControllerTests.Make(new FakeServices { Discord = discord });
        controller.DiscordAppId = Id;
        controller.DiscordPresenceEnabled = true;
        controller.GameVideoWanted = true;
        var shown = discord.Updates[^1]!;
        Assert.Null(shown.Details);
        Assert.Null(shown.State);
        Assert.Null(shown.SmallImage);
        Assert.Null(shown.LargeText);
        controller.Dispose();
    }
}
