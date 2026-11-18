// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

/// <summary>
/// "Yayın penceresi"ndeki çevirinin metin kuralları: baştaki konuşan adı ve satırlar. Ad ayrı renkte/boyda çizilir;
/// alt alta bölünmüş cümle tek satıra birleşir ama yeni bir konuşanla başlayan satır kendi satırında kalır.
/// </summary>
public static class StreamSubtitleText
{
    /// <summary>
    /// "Miles: ..." biçiminde baştaki konuşan adı: en çok 3 sözcük, her sözcük büyük harfle başlar ("Peter İki kelime:"
    /// ad değildir). İki nokta yoksa <paramref name="known"/> adlardan biriyle başlıyorsa da ad sayılır (OCR iki noktayı
    /// kaçırınca: "Peter İki kelime…"). Yoksa null.
    /// </summary>
    public static (string Speaker, string Line)? SplitSpeaker(string text, IEnumerable<string>? known = null)
    {
        var colon = text.IndexOf(':');
        if (colon >= 1 && colon <= 24 && colon + 1 < text.Length)
        {
            var name = text[..colon].Trim();
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!name.Contains('\n') && words.Length is >= 1 and <= 3
                && words.All(word => char.IsUpper(word[0]))
                && name.All(c => char.IsLetter(c) || c is ' ' or '.' or '\'' or '-'))
                return (name, text[(colon + 1)..].TrimStart());
        }
        if (known is not null)
            foreach (var name in known.OrderByDescending(name => name.Length))
                if (text.Length > name.Length + 1 && text.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
                    return (name, text[(name.Length + 1)..].TrimStart());
        return null;
    }

    /// <summary>
    /// Alt alta bölünmüş satırlar tek satıra (genişlik yetmezse yazı kendisi kırılır); ancak yeni bir konuşanla
    /// başlayan satır ("Peter: …" altında "Miles: Ee…") kendi satırında kalır.
    /// </summary>
    public static List<string> Lines(string text)
    {
        var lines = new List<string>();
        foreach (var part in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (lines.Count > 0 && SplitSpeaker(part) is null) lines[^1] += " " + part;
            else lines.Add(part);
        }
        return lines;
    }
}
