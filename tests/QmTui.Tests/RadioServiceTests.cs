using System.Collections.Generic;
using QmTui.Models;
using QmTui.Services;
using Xunit;

namespace QmTui.Tests;

[Collection("RadioService")]
public class RadioServiceTests
{
    private static RadioService Service => RadioService.Instance;

    private static List<Song> MakeSongs(int count, string prefix = "radio_mid")
    {
        var list = new List<Song>();
        for (int i = 1; i <= count; i++)
        {
            list.Add(new Song($"{prefix}_{i}", $"Radio Song {i}", $"Artist {i}", "Album", 200, "", i));
        }
        return list;
    }

    [Fact]
    public void AppendUniqueSongs_DeduplicatesExistingMids()
    {
        Service.ResetForTest(MakeSongs(3));
        Assert.Equal(3, Service.QueueCount);

        var moreSongs = new List<Song>
        {
            new("radio_mid_2", "Duplicate 2", "Artist 2", "Album", 200, "", 2),
            new("radio_mid_4", "New 4", "Artist 4", "Album", 200, "", 4),
            new("radio_mid_5", "New 5", "Artist 5", "Album", 200, "", 5),
            new("", "Empty mid", "Artist", "Album", 200, "", 6)
        };

        int added = Service.AppendUniqueSongs(moreSongs);

        Assert.Equal(2, added);
        Assert.Equal(5, Service.QueueCount);
    }

    [Fact]
    public void PeekNextRadioTrack_DoesNotAdvanceCursor()
    {
        Service.ResetForTest(MakeSongs(3), startIndex: 0);

        var peeked = Service.PeekNextRadioTrack();
        Assert.NotNull(peeked);
        Assert.Equal("radio_mid_2", peeked.Mid);
        Assert.Equal(0, Service.CurrentIndex);

        var peekedAgain = Service.PeekNextRadioTrack();
        Assert.NotNull(peekedAgain);
        Assert.Equal("radio_mid_2", peekedAgain.Mid);
    }

    [Fact]
    public void PeekNextRadioTrack_AtEnd_ReturnsNull()
    {
        Service.ResetForTest(MakeSongs(2), startIndex: 1);
        Assert.Null(Service.PeekNextRadioTrack());
    }

    [Fact]
    public void IsCurrentSongInRadio_IdentifiesPresenceCorrectly()
    {
        Service.ResetForTest(MakeSongs(3), startIndex: 0);

        Assert.True(Service.IsCurrentSongInRadio(new Song("radio_mid_2", "Title", "Artist", "Album", 200, "", 2)));
        Assert.False(Service.IsCurrentSongInRadio(new Song("other_mid", "Other", "Artist", "Album", 200, "", 99)));
        Assert.False(Service.IsCurrentSongInRadio(null));
    }

    [Fact]
    public async Task GetNextRadioTrackAsync_AdvancesIndexAndPlayedCount()
    {
        Service.ResetForTest(MakeSongs(5), startIndex: 0, playedCount: 1);

        var next = await Service.GetNextRadioTrackAsync();

        Assert.NotNull(next);
        Assert.Equal("radio_mid_2", next.Mid);
        Assert.Equal(1, Service.CurrentIndex);
        Assert.Equal(2, Service.PlayedCount);
    }
}
