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
}
