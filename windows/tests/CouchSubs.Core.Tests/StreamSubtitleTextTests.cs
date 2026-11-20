// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class StreamSubtitleTextTests
{
    [Fact]
    public void SpeakerNeedsCapitalisedWords()
    {
        Assert.Equal(("Peter", "Hi!"), StreamSubtitleText.SplitSpeaker("Peter: Hi!"));
        Assert.Equal(("Norman Osborn", "Hazırız."), StreamSubtitleText.SplitSpeaker("Norman Osborn: Hazırız."));
        // OCR iki noktayı kaçırınca cümlenin içindeki iki nokta adı uzatmasın.
        Assert.Null(StreamSubtitleText.SplitSpeaker("Peter İki kelime: Yüzey gerilimi!"));
        Assert.Null(StreamSubtitleText.SplitSpeaker("Saat 10:30"));
        // Fork: çevrilmiş rol adları da ad.
        Assert.Equal(("Mağaza görevlisi", "Hoş geldiniz."), StreamSubtitleText.SplitSpeaker("Mağaza görevlisi: Hoş geldiniz."));
        Assert.Equal(("Polis 2", "Dur!"), StreamSubtitleText.SplitSpeaker("Polis 2: Dur!"));
    }

    [Fact]
    public void KnownSpeakerIsFoundWithoutTheColon()
    {
        string[] known = ["Peter", "Miles"];
        Assert.Equal(("Peter", "İki kelime: Yüzey gerilimi!"), StreamSubtitleText.SplitSpeaker("Peter İki kelime: Yüzey gerilimi!", known));
        Assert.Null(StreamSubtitleText.SplitSpeaker("Peterson geldi", known));
    }

    [Fact]
    public void WrappedSentenceIsJoinedButANewSpeakerKeepsItsLine()
    {
        Assert.Equal(["Spider-Man: Çünkü okul rehber öğretmeniyle görüşmem var."],
            StreamSubtitleText.Lines("Spider-Man: Çünkü okul rehber\nöğretmeniyle görüşmem var."));
        Assert.Equal(["Peter: kuvvetin etki ettiği hat. Kimse...", "Miles: Ee..."],
            StreamSubtitleText.Lines("Peter: kuvvetin etki ettiği hat. Kimse...\nMiles: Ee..."));
    }
}
