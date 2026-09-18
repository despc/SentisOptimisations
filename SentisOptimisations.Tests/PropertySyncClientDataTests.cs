using System;
using Optimizer.Optimizations;
using VRage.Library.Collections;
using Xunit;

namespace SentisOptimisations.Tests;

public class PropertySyncClientDataTests
{
    [Fact]
    public void Slots_behave_like_the_vanilla_256_array_for_outstanding_packets()
    {
        var random = new Random(3);
        var vanilla = new ulong[256];
        var outstanding = new bool[256];
        var table = new SmallBitField[PropertySyncClientData.InitialEntries * 2];
        for (var step = 0; step < 100000; step++)
        {
            var id = (byte)random.Next(256);
            if (random.Next(2) == 0)
            {
                var bits = (ulong)random.Next() << 20 | (uint)random.Next();
                vanilla[id] = bits;
                outstanding[id] = true;
                var index = PropertySyncClientData.FindOrAdd(ref table, id, out _);
                table[index].Bits = bits;
            }
            else if (outstanding[id])
            {
                Assert.Equal(vanilla[id], PropertySyncClientData.Take(table, id));
                outstanding[id] = false;
            }
            else
            {
                Assert.Equal(0UL, PropertySyncClientData.Take(table, id));
            }
        }
        Assert.True(table.Length <= PropertySyncClientData.MaxEntries * 2);
    }

    [Fact]
    public void Table_stays_small_when_acks_keep_up()
    {
        var table = new SmallBitField[PropertySyncClientData.InitialEntries * 2];
        for (var packet = 0; packet < 10000; packet++)
        {
            var id = (byte)packet;
            var index = PropertySyncClientData.FindOrAdd(ref table, id, out _);
            table[index].Bits = (ulong)packet + 1;
            // Acks arrive three packets later.
            if (packet >= 3) Assert.Equal((ulong)packet - 2, PropertySyncClientData.Take(table, (byte)(packet - 3)));
        }
        Assert.Equal(PropertySyncClientData.InitialEntries * 2, table.Length);
    }

    [Fact]
    public void Resend_in_a_packet_with_the_same_id_overwrites_the_entry()
    {
        var table = new SmallBitField[PropertySyncClientData.InitialEntries * 2];
        table[PropertySyncClientData.FindOrAdd(ref table, 7, out _)].Bits = 1;
        table[PropertySyncClientData.FindOrAdd(ref table, 7, out _)].Bits = 2;
        Assert.Equal(2UL, PropertySyncClientData.Take(table, 7));
        Assert.Equal(0UL, PropertySyncClientData.Take(table, 7));
    }
}
