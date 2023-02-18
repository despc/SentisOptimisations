using System;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SentisOptimisations;
using VRage.Game;

namespace SentisOptimisationsPlugin.AllGridsActions
{
    public class GridAutoRenamer
    {
        private static readonly Random Random = new Random();

        public void CheckAndRename(MyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose)
            {
                return;
            }

            var gridDisplayName = grid.DisplayName;

            if (gridDisplayName.Contains("Small Grid") || gridDisplayName.Contains("Static Grid")
                                                       || gridDisplayName.Contains("Large Grid")
                                                       || gridDisplayName.Contains("Малая структура")
                                                       || gridDisplayName.Contains("Большая структура")
                                                       || gridDisplayName.Contains("Статичная структура"))
            {
                RenameGrid(grid);
            }
        }

        private void RenameGrid(MyCubeGrid grid)
        {
            var ownerId = PlayerUtils.GetOwner(grid);
            if (ownerId == 0)
            {
                ownerId = grid.GetBlocks().First().BuiltBy;
            }

            var ownerName = "Nobody";
            if (ownerId != 0)
            {
                MyIdentity identity = Sync.Players.TryGetIdentity(ownerId);
                ownerName = identity.DisplayName;
            }

            var fatBlocks = grid.GetFatBlocks();
            if (fatBlocks.Count == 1)
            {
                var subtypeName = fatBlocks[0].BlockDefinition.Id.SubtypeName;
                if (subtypeName.Contains("Wheel"))
                {
                    grid.DisplayName = ownerName + "_" + "Wheel";
                    return;
                }

                if (subtypeName.Contains("Piston"))
                {
                    grid.DisplayName = ownerName + "_" + "Piston";
                    return;
                }

                if (subtypeName.Contains("Rotor"))
                {
                    grid.DisplayName = ownerName + "_" + "Rotor";
                    return;
                }
            }

            var prefix = "SG";
            if (grid.GridSizeEnum == MyCubeSize.Large)
            {
                prefix = "LG";
            }

            grid.DisplayName = ownerName + "_" + prefix + "_" + Random.Next(0, 1000);
        }
    }
}