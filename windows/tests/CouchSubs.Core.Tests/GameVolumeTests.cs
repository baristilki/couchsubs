// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class GameVolumeTests
{
    [Fact]
    public void FullVolumeLeavesSamplesUntouched()
    {
        short[] pcm = [1000, -1000, short.MaxValue, short.MinValue];
        GameVolume.Apply(pcm, 1);
        Assert.Equal(new short[] { 1000, -1000, short.MaxValue, short.MinValue }, pcm);
    }

    [Fact]
    public void ZeroSilencesAndHalfIsQuieter()
    {
        short[] silent = [1000, -1000];
        GameVolume.Apply(silent, 0);
        Assert.Equal(new short[] { 0, 0 }, silent);

        short[] half = [1000, -1000];
        GameVolume.Apply(half, 0.5);
        Assert.Equal(new short[] { 250, -250 }, half);
    }
}
