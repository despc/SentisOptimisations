using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Contracts;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The economy tick spread over frames, a faction a frame.
    ///
    /// Once an economy tick (every 10 minutes by default) <c>MySessionComponentEconomy.UpdateStations</c> goes through
    /// every faction in one frame: its balance, each of its stations, the contracts of each station and the items of its
    /// stores - 50 ms of one frame on the stand even with <see cref="PrefabPriceOnce"/>. Here the tick only gets ready
    /// (the old contracts cleaned, the stations' contracts counted, the factions listed, as the game does first), and the
    /// frames after do a step each - a faction's balance, each of its stations with its contracts, its stores - with the
    /// game's own methods in the game's order.
    /// </summary>
    [PatchShim]
    public static class EconomySpread
    {
        private delegate void CountsFn(MySessionComponentContractSystem system, ref Dictionary<long, int> counts, ref Dictionary<long, List<MyContract>> lists);
        private delegate void CreateFn(MySessionComponentContractSystem system, MyContractGenerator generator, MyFaction faction, IMyFactionStation station,
            int count, ref List<MyContract> existing);

        private static Action<MySessionComponentContractSystem> _clean;
        private static CountsFn _counts;
        private static CreateFn _create;
        private static Action<MyFactionStation, MyFaction> _stationUpdate;
        private static Action<MySessionComponentEconomy, MyFaction> _balance;
        private static FieldInfo _storeItems, _firstGeneration;

        private sealed class Work
        {
            public MySessionComponentEconomy Economy;
            public MySessionComponentContractSystem Contracts;
            public Dictionary<long, int> Counts;
            public Dictionary<long, List<MyContract>> Lists;
            public MyContractGenerator Generator;
            public Queue<MyFaction> Factions;
            /// <summary>The steps left of the faction under way: its stations one at a time, then its stores.</summary>
            public Queue<Action> Steps = new Queue<Action>();
            public int StepsDone, Frames;
            public double MaxStepMs, StartMs;
            public string MaxStep = "";
        }

        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        // game thread only
        private static Work _work;
        [ThreadStatic] private static bool _replaying;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("EconomySpread", ctx, PatchImpl);

        /// <summary>The game's methods found (false: something is missing and nothing is patched).</summary>
        public static bool Bind()
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var contracts = typeof(MySessionComponentContractSystem);
            var economy = typeof(MySessionComponentEconomy);
            var clean = contracts.GetMethod("CleanOldContracts", any, null, Type.EmptyTypes, null);
            var counts = contracts.GetMethod("GetAvailableContractCountsByStation", any);
            var create = contracts.GetMethod("CreateContractsForStation", any);
            var stationUpdate = typeof(MyFactionStation).GetMethod("Update", any, null, new[] { typeof(MyFaction) }, null);
            var balance = economy.GetMethod("UpdateUpdateFactionBalance", any, null, new[] { typeof(MyFaction) }, null);
            _storeItems = economy.GetField("m_storeItemsGenerator", any);
            _firstGeneration = economy.GetField("m_stationStoreItemsFirstGeneration", any);
            if (clean == null || counts == null || create == null || stationUpdate == null || balance == null || _storeItems == null || _firstGeneration == null)
                return false;
            _clean = (Action<MySessionComponentContractSystem>)Delegate.CreateDelegate(typeof(Action<MySessionComponentContractSystem>), clean);
            _counts = (CountsFn)Delegate.CreateDelegate(typeof(CountsFn), counts);
            _create = (CreateFn)Delegate.CreateDelegate(typeof(CreateFn), create);
            _stationUpdate = (Action<MyFactionStation, MyFaction>)Delegate.CreateDelegate(typeof(Action<MyFactionStation, MyFaction>), stationUpdate);
            _balance = (Action<MySessionComponentEconomy, MyFaction>)Delegate.CreateDelegate(typeof(Action<MySessionComponentEconomy, MyFaction>), balance);
            return true;
        }

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (!Bind()) throw new MissingMemberException("EconomySpread: the economy's methods are not all there");
            var economy = typeof(MySessionComponentEconomy);
            var updateStations = economy.GetMethod("UpdateStations", any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("MySessionComponentEconomy.UpdateStations");
            var after = economy.GetMethod("UpdateAfterSimulation", any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("MySessionComponentEconomy.UpdateAfterSimulation");
            ctx.GetPattern(updateStations).Prefixes.Add(typeof(EconomySpread).GetMethod(nameof(UpdateStationsPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(after).Suffixes.Add(typeof(EconomySpread).GetMethod(nameof(AfterSuffix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool UpdateStationsPrefix(MySessionComponentEconomy __instance)
        {
            if (_replaying) return true;
            var contracts = MySession.Static?.GetComponent<MySessionComponentContractSystem>();
            if (contracts == null || __instance.EconomyDefinition == null) return true;
            // a tick not done yet when the next one comes (a forced tick): its rest done now, as the game would have
            if (_work != null && ReferenceEquals(_work.Economy, __instance))
            {
                Log.Warn($"EconomySpread: the last tick not done ({_work.Factions.Count} factions, {_work.Steps.Count} steps left); done now");
                while (_work != null) Step();
            }
            _work = null;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            // what the game does first, all in this frame: the old contracts cleaned, the stations' contracts counted
            _clean(contracts);
            var counts = new Dictionary<long, int>();
            var lists = new Dictionary<long, List<MyContract>>();
            _counts(contracts, ref counts, ref lists);
            _work = new Work
            {
                Economy = __instance, Contracts = contracts, Counts = counts, Lists = lists,
                Generator = new MyContractGenerator(__instance.EconomyDefinition),
                Factions = new Queue<MyFaction>(MySession.Static.Factions.Select(f => f.Value)),
                StartMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
            };
            return false;
        }

        private static void AfterSuffix(MySessionComponentEconomy __instance)
        {
            if (_work == null) return;
            // a world unloaded in between: its work is not this one's
            if (!ReferenceEquals(_work.Economy, __instance)) { _work = null; return; }
            var work = _work;
            work.Frames++;
            Step();
            if (_work == null)
                if (global::SentisOptimisations.DiagLog.On) Log.Info($"EconomySpread: tick done in {work.Frames} frames, {work.StepsDone} steps; start {work.StartMs:0.0} ms, the longest step {work.MaxStepMs:0.0} ms ({work.MaxStep})");
        }

        /// <summary>One step of the game's loop: the next faction's balance (its stations and stores queued), a station, or a faction's stores.</summary>
        private static void Step()
        {
            var work = _work;
            if (work.Steps.Count == 0)
            {
                if (work.Factions.Count == 0) { _work = null; return; }
                var faction = work.Factions.Dequeue();
                Run(work, "balance " + faction.Tag, () => _balance(work.Economy, faction));
                foreach (var station in faction.Stations.ToList())
                    if (station is MyFactionStation factionStation)
                        work.Steps.Enqueue(() => Run(work, "station of " + faction.Tag, () => Station(work, faction, factionStation)));
                work.Steps.Enqueue(() => Run(work, "stores of " + faction.Tag, () =>
                {
                    ((MyStoreItemsGenerator)_storeItems.GetValue(work.Economy)).Update(faction, (bool)_firstGeneration.GetValue(work.Economy));
                    _firstGeneration.SetValue(work.Economy, false);
                }));
            }
            else work.Steps.Dequeue()();
            if (work.Steps.Count == 0 && work.Factions.Count == 0) _work = null;
        }

        private static void Station(Work work, MyFaction faction, MyFactionStation station)
        {
            _stationUpdate(station, faction);
            if (!work.Counts.ContainsKey(station.Id))
            {
                var existing = new List<MyContract>();
                _create(work.Contracts, work.Generator, faction, station, 0, ref existing);
                work.Lists[station.Id] = existing;
            }
            else
            {
                var existing = work.Lists[station.Id];
                _create(work.Contracts, work.Generator, faction, station, work.Counts[station.Id], ref existing);
            }
        }

        private static void Run(Work work, string what, Action step)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            _replaying = true;
            try
            {
                step();
            }
            finally
            {
                _replaying = false;
            }
            var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            work.StepsDone++;
            if (ms > work.MaxStepMs) { work.MaxStepMs = ms; work.MaxStep = what; }
        }
    }
}
