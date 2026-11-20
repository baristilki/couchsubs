// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using System.Text.Json;

namespace CouchSubs.Core;

/// <summary>
/// Fork: yapay zekâ çevirisinin oyun sözlüğü. <c>%LOCALAPPDATA%\CouchSubs\ai-glossary\*.json</c> dosyaları
/// <see cref="Glossary"/> biçimindedir (<c>terms</c>, <c>keep</c>); <c>"game"</c> alanı varsa yalnız o oyunda
/// (<see cref="GameName.Key"/> ile, "GTA 6" = "Grand Theft Auto VI"), yoksa her oyunda kullanılır. Ücretsiz çevirinin
/// genel sözlüğüne karışmaz: satırda geçen terimler yapay zekâya "bu Türkçeyi kullan" diye söylenir.
/// Dosyalar en çok beş saniyede bir yeniden okunur; uygulama açıkken düzenlenebilir.
/// </summary>
public sealed class AiGlossary(string? directory, Func<DateTimeOffset>? clock = null)
{
    public static string UserDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchSubs", "ai-glossary");

    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object gate = new();
    private DateTimeOffset checkedAt = DateTimeOffset.MinValue;
    private string stamp = "";
    private List<(string? Game, Glossary Glossary)> files = [];
    private readonly Dictionary<string, Glossary> byGame = new(StringComparer.Ordinal);

    /// <summary>Oyunun sözlüğü (oyunsuz dosyalar ve o oyunun dosyaları birleşik); hiç terim yoksa <see cref="Glossary.Empty"/>.</summary>
    public Glossary For(string? game)
    {
        var key = game is null ? "" : GameName.Key(game);
        lock (gate)
        {
            Refresh();
            if (byGame.TryGetValue(key, out var known)) return known;
            var parts = files.Where(file => file.Game is null || file.Game == key).Select(file => file.Glossary).ToList();
            var merged = parts.Count == 0 ? Glossary.Empty : parts.Count == 1 ? parts[0] : Glossary.Merge(parts);
            byGame[key] = merged;
            return merged;
        }
    }

    /// <summary>Satırlarda geçen terimler ve Türkçeleri (aynı terim bir kez, metindeki sırayla).</summary>
    public static IReadOnlyList<(string English, string Turkish)> Terms(Glossary glossary, IEnumerable<string> texts)
    {
        var found = new List<(string, string)>();
        if (glossary.IsEmpty) return found;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
            foreach (var match in glossary.Find(text))
                if (seen.Add(match.English)) found.Add((match.English, match.Turkish));
        return found;
    }

    private void Refresh()
    {
        var time = now();
        if (time - checkedAt < CheckEvery) return;
        checkedAt = time;
        var paths = Glossary.UserFiles(directory).ToList();
        var current = string.Join("|", paths.Select(path =>
        {
            try { return path + "@" + File.GetLastWriteTimeUtc(path).Ticks; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return path; }
        }));
        if (current == stamp) return;
        stamp = current;
        byGame.Clear();
        var loaded = new List<(string?, Glossary)>();
        foreach (var path in paths)
        {
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                loaded.Add((GameOf(json), Glossary.Parse(json)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException) { }
        }
        files = loaded;
    }

    private static string? GameOf(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("game", out var game)
            && game.ValueKind == JsonValueKind.String && GameName.Key(game.GetString()!) is { Length: > 0 } key ? key : null;
    }
}
