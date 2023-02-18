using System;

namespace SentisOptimisationsPlugin
{
    public class Donation
    {
        public Donation(long steamId, DonationType type, int count, DateTime before)
        {
            SteamId = steamId;
            Type = type;
            Count = count;
            Before = before;
        }

        public long SteamId;
        public DonationType Type;
        public int Count; // for pcu
        public DateTime Before;

        public enum DonationType
        {
            WELDER,
            GRINDER,
            GRINDER_WELDER,
            PCU
        }
    }
}