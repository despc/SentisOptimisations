using System;
using System.Collections.Generic;
using SentisGameplayImprovements.Assholes;
using VRageMath;
using Xunit;

namespace SentisOptimisations.Tests;

/// <summary>SentisGameplayImprovements' stuck-in-voxels handling: which grids are stuck, where they go.</summary>
public class StuckGridTests
{
    private const long Heavy = StuckGridTracker.ContactsPerSecond;

    private static Dictionary<long, long> Second(params (long Id, long Contacts)[] counts)
    {
        var d = new Dictionary<long, long>();
        foreach (var (id, contacts) in counts) d[id] = contacts;
        return d;
    }

    [Fact]
    public void A_grid_grinding_for_the_seconds_in_a_row_is_stuck_once()
    {
        var tracker = new StuckGridTracker();
        for (var s = 1; s < StuckGridTracker.StuckSeconds; s++)
        {
            Assert.Empty(tracker.Update(Second((1, Heavy))));
            Assert.Equal(s, tracker.Streak(1));
        }
        Assert.Equal(new List<long> { 1 }, tracker.Update(Second((1, Heavy))));
        Assert.Equal(0, tracker.Streak(1));
    }

    [Fact]
    public void A_calm_second_starts_the_count_over()
    {
        var tracker = new StuckGridTracker();
        for (var round = 0; round < 10; round++)
        {
            for (var s = 1; s < StuckGridTracker.StuckSeconds; s++)
                Assert.Empty(tracker.Update(Second((1, Heavy))));
            // a second under the threshold, or with no contact at all
            Assert.Empty(tracker.Update(round % 2 == 0 ? Second((1, Heavy - 1)) : Second()));
            Assert.Equal(0, tracker.Streak(1));
        }
    }

    [Fact]
    public void Light_contact_never_counts()
    {
        var tracker = new StuckGridTracker();
        for (var s = 0; s < 100; s++)
            Assert.Empty(tracker.Update(Second((1, Heavy - 1))));
        Assert.Equal(0, tracker.Streak(1));
    }

    [Fact]
    public void Grids_are_counted_each_on_its_own()
    {
        var tracker = new StuckGridTracker();
        for (var s = 1; s < StuckGridTracker.StuckSeconds; s++)
            tracker.Update(Second((1, Heavy), (2, s % 2 == 0 ? Heavy : 0)));
        var stuck = tracker.Update(Second((1, Heavy), (2, Heavy)));
        Assert.Equal(new List<long> { 1 }, stuck);
        // grid 2 ground every other second: only its last two seconds are in a row
        Assert.Equal(2, tracker.Streak(2));
    }

    [Fact]
    public void The_destination_is_the_distance_away_and_free()
    {
        var random = new Random(3);
        var centre = new Vector3D(1000, -2000, 3000);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(Voxels.TryPickDestination(centre, Voxels.TeleportDistance, 60, random, s => s.Center.Y > centre.Y, out var at));
            Assert.Equal(Voxels.TeleportDistance, Vector3D.Distance(at, centre), 6);
            Assert.True(at.Y > centre.Y);
        }
    }

    [Fact]
    public void With_no_free_place_there_is_no_destination()
    {
        var tries = 0;
        Assert.False(Voxels.TryPickDestination(Vector3D.Zero, 1000, 60, new Random(1), s => { tries++; return false; }, out _));
        Assert.Equal(Voxels.PlacementTries, tries);
    }
}
