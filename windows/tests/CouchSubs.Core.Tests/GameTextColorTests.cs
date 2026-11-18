// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class GameTextColorTests
{
    private const int Width = 640, Height = 360;
    private static readonly CropRegion Bottom = new(Top: 0.7, Bottom: 0.95, Left: 0.1, Right: 0.9);

    private static byte[] Fill(byte r, byte g, byte b)
    {
        var pixels = new byte[Width * Height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
        return pixels;
    }

    /// <summary>Koyu kenarlı yazı satırı gibi: dikey çizgiler (2 px renkli, yanlarında 2 px siyah).</summary>
    private static void Text(byte[] pixels, int fromX, int toX, int y0, int y1, byte r, byte g, byte b)
    {
        for (var y = y0; y < y1; y++)
            for (var x = fromX; x < toX; x++)
            {
                var phase = (x - fromX) % 6;
                var i = ((y * Width) + x) * 4;
                if (phase < 2) (pixels[i], pixels[i + 1], pixels[i + 2]) = (b, g, r);
                else if (phase < 4) (pixels[i], pixels[i + 1], pixels[i + 2]) = (0, 0, 0);
            }
    }

    [Fact]
    public void YellowSubtitleIsRecognised()
    {
        var frame = Fill(60, 90, 120); // mavimsi sahne
        Text(frame, 120, 520, 280, 300, 255, 225, 40);
        var color = GameTextColor.Sample(frame, Width, Height, Bottom);
        Assert.NotNull(color);
        Assert.True(color.Value.R > 230 && color.Value.G > 190 && color.Value.B < 90, color.ToString());
    }

    [Fact]
    public void WhiteSubtitleBecomesWhite()
    {
        var frame = Fill(60, 90, 120);
        Text(frame, 120, 520, 280, 300, 235, 235, 230);
        Assert.Equal(GameTextColor.White, GameTextColor.Sample(frame, Width, Height, Bottom));
    }

    [Fact]
    public void BrightBackgroundWithoutOutlineIsNotText()
    {
        // Parlak gökyüzü: parlak ama yanında koyu piksel yok; yazı sayılmaz.
        Assert.Null(GameTextColor.Sample(Fill(250, 250, 255), Width, Height, Bottom));
        Assert.Null(GameTextColor.Sample(Fill(20, 20, 20), Width, Height, Bottom));
    }

    [Fact]
    public void DialogueColourWinsOverAShortSpeakerName()
    {
        var frame = Fill(40, 40, 40);
        Text(frame, 120, 180, 280, 300, 90, 200, 255); // kısa mavi ad
        Text(frame, 186, 520, 280, 300, 240, 240, 240); // uzun beyaz cümle
        Assert.Equal(GameTextColor.White, GameTextColor.Sample(frame, Width, Height, Bottom));
    }

    [Fact]
    public void ColouredSpeakerNameIsReadSeparately()
    {
        var frame = Fill(40, 40, 40);
        Text(frame, 120, 200, 280, 300, 80, 160, 255); // mavi "Spider-Man:"
        Text(frame, 206, 520, 280, 300, 240, 240, 240); // beyaz cümle
        var colors = GameTextColor.SampleColors(frame, Width, Height, Bottom);
        Assert.NotNull(colors);
        Assert.Equal(GameTextColor.White, colors.Value.Text);
        Assert.NotNull(colors.Value.Speaker);
        var speaker = colors.Value.Speaker!.Value;
        Assert.True(speaker.B > 230 && speaker.R < 120, speaker.ToString());
    }

    [Fact]
    public void LongNameOnAShortLineDoesNotColourTheSentence()
    {
        // "Spider-Man: Oh." — turuncu ad cümleden uzun; cümle yine beyaz, ad turuncu.
        var frame = Fill(40, 40, 40);
        Text(frame, 120, 320, 280, 300, 255, 120, 90);
        Text(frame, 326, 380, 280, 300, 240, 240, 240);
        var colors = GameTextColor.SampleColors(frame, Width, Height, Bottom)!.Value;
        Assert.Equal(GameTextColor.White, colors.Text);
        Assert.True(colors.Speaker!.Value.R > 230 && colors.Speaker.Value.B < 140, colors.Speaker.ToString());
    }

    [Fact]
    public void SingleColourLineHasNoSpeakerColour()
    {
        var frame = Fill(40, 40, 40);
        Text(frame, 120, 520, 280, 300, 240, 240, 240);
        Assert.Null(GameTextColor.SampleColors(frame, Width, Height, Bottom)!.Value.Speaker);
    }

    [Fact]
    public void BookKeepsAColourPerSpeaker()
    {
        var book = new GameTextColor.Book();
        var blue = new GameTextColor.Rgb(80, 160, 255);
        var orange = new GameTextColor.Rgb(255, 120, 90);
        var sample = new GameTextColor.Colors(GameTextColor.White, blue);
        Assert.False(book.Offer(sample, "Miles"));
        Assert.True(book.Offer(sample, "Miles")); // ad renkleri 2 örnekte
        book.Offer(new GameTextColor.Colors(GameTextColor.White, orange), "Ganke");
        book.Offer(new GameTextColor.Colors(GameTextColor.White, orange), "Ganke");
        Assert.Equal(blue, book.Speaker("miles"));
        Assert.Equal(orange, book.Speaker("Ganke"));
        Assert.Null(book.Speaker("Peter"));
        Assert.Equal("#50A0FF", book.SpeakerHexes()["Miles"]);
        Assert.Equal(GameTextColor.White, book.Text);
        book.Reset();
        Assert.Empty(book.SpeakerHexes());
    }

    [Fact]
    public void TrackerSwitchesOnlyAfterRepeatedSamples()
    {
        var tracker = new GameTextColor.Tracker();
        var yellow = new GameTextColor.Rgb(255, 225, 40);
        Assert.False(tracker.Offer(yellow));
        Assert.False(tracker.Offer(null)); // yazı yok: sayaç sıfırlanmaz, renk korunur
        Assert.False(tracker.Offer(yellow));
        Assert.True(tracker.Offer(yellow));
        Assert.Equal(yellow, tracker.Current);

        // Tek karelik sapma rengi değiştirmez.
        Assert.False(tracker.Offer(GameTextColor.White));
        Assert.False(tracker.Offer(yellow));
        Assert.Equal(yellow, tracker.Current);
        tracker.Reset();
        Assert.Equal(GameTextColor.White, tracker.Current);
    }
}
