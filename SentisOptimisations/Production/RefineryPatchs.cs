using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NAPI;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class RefineryPatchs
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var methodInfo = typeof(MyRefinery).GetMethod("RebuildQueue", BindingFlags.DeclaredOnly
                                                                               | BindingFlags.Instance |
                                                                               BindingFlags.NonPublic);
            ctx.GetPattern(methodInfo).Prefixes.Add(
                typeof(RefineryPatchs).GetMethod(nameof(DoUpdateTimerTickPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool DoUpdateTimerTickPatch(MyRefinery __instance)
        {
          __instance.easySetField("m_queueNeedsRebuild", false);
          __instance.ClearQueue(false);
          var tmpSortedBp = ((List<KeyValuePair<int, MyBlueprintDefinitionBase>>)__instance.easyGetField("m_tmpSortedBlueprints"));
          tmpSortedBp.Clear();
          MyPhysicalInventoryItem[] array = __instance.InputInventory.GetItems().ToArray();
          for (int key = 0; key < array.Length; ++key)
          {
            var instanceMRefineryDef = ((MyRefineryDefinition)__instance.BlockDefinition);
            foreach (MyBlueprintClassDefinition bp in instanceMRefineryDef.BlueprintClasses)
            {
              foreach (MyBlueprintDefinitionBase blueprintDefinitionBase in bp)
              {
                bool flag = false;
                MyDefinitionId other = new MyDefinitionId(array[key].Content.TypeId, array[key].Content.SubtypeId);
                for (int index2 = 0; index2 < blueprintDefinitionBase.Prerequisites.Length; ++index2)
                {
                  if (blueprintDefinitionBase.Prerequisites[index2].Id.Equals(other))
                  {
                    flag = true;
                    break;
                  }
                }

                if (flag)
                {
                  tmpSortedBp.Add(
                    new KeyValuePair<int, MyBlueprintDefinitionBase>(key, blueprintDefinitionBase));
                  break;
                }
              }
            }
          }

          for (int index = 0; index < tmpSortedBp.Count; ++index)
          {
            MyBlueprintDefinitionBase blueprint = tmpSortedBp[index].Value;
            MyFixedPoint myFixedPoint = MyFixedPoint.MaxValue;
            bool skipBp = false;
            foreach (MyBlueprintDefinitionBase.Item prerequisite in blueprint.Prerequisites)
            {
              
              var listOfSources = array.Where(item => item.Content.SubtypeName.Equals(prerequisite.Id.SubtypeName)).ToList();
              if (listOfSources.Count == 0)
              {
                skipBp = true;
                break;
              }
              MyFixedPoint amount = listOfSources[0].Amount;;
              if (amount == (MyFixedPoint)0)
              {
                myFixedPoint = (MyFixedPoint)0;
                skipBp = true;
                break;
              }

              myFixedPoint = MyFixedPoint.Min(amount * (1f / (float)prerequisite.Amount), myFixedPoint);
            }

            if (skipBp)
            {
              continue;
            }
            if (blueprint.Atomic)
              myFixedPoint = MyFixedPoint.Floor(myFixedPoint);
            if (myFixedPoint > (MyFixedPoint)0 && myFixedPoint != MyFixedPoint.MaxValue)
              __instance.InsertQueueItemRequest(-1, blueprint, myFixedPoint);
          }

          tmpSortedBp.Clear();
          return false;
        }
    }
}