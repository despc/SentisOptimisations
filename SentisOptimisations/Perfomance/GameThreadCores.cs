using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Keeps the game thread on the CPU's fastest cores. On a hybrid CPU (Intel 12th gen and later) Windows also runs it
    /// on the efficiency cores, at half the speed, and moves it between the performance ones, to cores that boost lower
    /// than the "favored" ones (on an i7-13700K logical 8-11 reach 5.4 GHz). The thread's CPU sets are set once, from
    /// the first frame: the performance cores of the highest efficiency class, of them the ones of the highest
    /// scheduling class (favored), at least two physical cores, one logical processor of each - the sibling would share
    /// the core. A CPU whose cores are all alike is left alone. Windows 10 and later; game thread. Off with
    /// <c>Game thread on fast cores</c>; as it is done once, a change takes effect after a restart.
    /// </summary>
    public static class GameThreadCores
    {
        private const int MinCores = 2;
        private static bool _done;

        public static void Apply()
        {
            if (_done) return;
            _done = true;
            try
            {
                if (SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.GameThreadOnFastCores == false)
                {
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Info("Game thread left to Windows (Game thread on fast cores is off)");
                    return;
                }
                var sets = Read();
                if (sets.Count == 0) return;
                var ids = Choose(sets);
                if (ids == null) return;
                if (!SetThreadSelectedCpuSets(GetCurrentThread(), ids.Select(s => s.Id).ToArray(), (uint)ids.Count))
                {
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Warn($"Game thread cores: SetThreadSelectedCpuSets failed, error {Marshal.GetLastWin32Error()}");
                    return;
                }
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Info(
                    $"Game thread on logical processors {string.Join(", ", ids.Select(s => s.Logical))} of {sets.Count}");
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "Game thread cores");
            }
        }

        /// <summary>The CPU sets to keep the game thread on, or null: the cores are all alike.</summary>
        public static List<CpuSet> Choose(List<CpuSet> sets)
        {
            var usable = sets.Where(s => !s.Allocated && !s.RealTime).ToList();
            if (usable.Count == 0) return null;
            var topEfficiency = usable.Max(s => s.Efficiency);
            // one logical processor per physical core, the core's best
            var cores = usable.Where(s => s.Efficiency == topEfficiency)
                .GroupBy(s => new { s.Group, s.Core })
                .Select(g => g.OrderByDescending(s => s.Scheduling).ThenBy(s => s.Logical).First())
                .OrderByDescending(s => s.Scheduling).ThenBy(s => s.Group).ThenBy(s => s.Logical)
                .ToList();
            var chosen = new List<CpuSet>();
            foreach (var cls in cores.Select(s => s.Scheduling).Distinct())
            {
                chosen.AddRange(cores.Where(s => s.Scheduling == cls));
                if (chosen.Count >= MinCores) break;
            }
            var allCores = usable.Select(s => new { s.Group, s.Core }).Distinct().Count();
            return chosen.Count >= allCores ? null : chosen;
        }

        public struct CpuSet
        {
            public uint Id;
            public ushort Group;
            public byte Logical, Core, Efficiency, Scheduling;
            public bool Allocated, RealTime;
        }

        private static List<CpuSet> Read()
        {
            var result = new List<CpuSet>();
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
            if (length == 0) return result;
            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return result;
                // SYSTEM_CPU_SET_INFORMATION: Size, Type, then the CpuSet member; entries may grow in later Windows
                for (var offset = 0; offset + 24 <= length;)
                {
                    var entry = buffer + offset;
                    var size = Marshal.ReadInt32(entry, 0);
                    if (size <= 0) break;
                    if (Marshal.ReadInt32(entry, 4) == 0 /* CpuSetInformation */)
                    {
                        var flags = Marshal.ReadByte(entry, 19);
                        result.Add(new CpuSet
                        {
                            Id = (uint)Marshal.ReadInt32(entry, 8),
                            Group = (ushort)Marshal.ReadInt16(entry, 12),
                            Logical = Marshal.ReadByte(entry, 14),
                            Core = Marshal.ReadByte(entry, 15),
                            Efficiency = Marshal.ReadByte(entry, 18),
                            Allocated = (flags & 2) != 0 && (flags & 4) == 0,
                            RealTime = (flags & 8) != 0,
                            Scheduling = Marshal.ReadByte(entry, 20),
                        });
                    }
                    offset += size;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint[] cpuSetIds, uint cpuSetIdCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
    }
}
