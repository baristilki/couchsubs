// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class GameTextSizeTests
{
    [Fact]
    public void LineCountIsEstimatedFromTheBoxHeight()
    {
        // Tek satır (~45 px / 1080) ve iki satır aynı satır yüksekliğini verir.
        var one = GameTextSize.LineHeight(new TextBox(0.2, 0.86, 0.8, 0.86 + 0.042))!.Value;
        var two = GameTextSize.LineHeight(new TextBox(0.2, 0.82, 0.8, 0.82 + 0.084))!.Value;
        Assert.Equal(one, two, 3);
        Assert.Null(GameTextSize.LineHeight(new TextBox(0.2, 0.5, 0.8, 0.5)));
        Assert.Null(GameTextSize.LineHeight(new TextBox(0, 0, 1, 0.9)));
    }

    [Fact]
    public void OneBadReadingDoesNotMoveTheSize()
    {
        var size = new GameTextSize();
        Assert.Null(size.Current);
        size.Offer(new TextBox(0.2, 0.86, 0.8, 0.90));
        size.Offer(new TextBox(0.2, 0.86, 0.8, 0.90));
        size.Offer(new TextBox(0.2, 0.86, 0.8, 0.90));
        size.Offer(new TextBox(0.2, 0.86, 0.8, 0.875)); // yarım okunmuş satır
        Assert.Equal(0.04, size.Current!.Value, 3);
        size.Reset();
        Assert.Null(size.Current);
    }
}
