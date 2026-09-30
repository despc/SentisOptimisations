using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Havok;
using Torch.Managers.PatchManager;
using VRage.Game.Models;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// The collision shapes of a model are not reference counted: they live as long as the model, whatever number of
    /// blocks uses them.
    ///
    /// A grid wraps the model collision of each of its blocks (the model's shape, or each child of its list or MOPP
    /// shape) in a transform shape, and each wrapper takes a reference on that one shared Havok shape. Havok's
    /// reference count is the low 16 bits of the word after the vtable (Havok.dll's HkShape_AddReference: a
    /// compare-exchange that carries nothing past 16 bits), so 65536 blocks of one model bring it round to where it
    /// was, and when removals take it through 0 Havok deletes a shape tens of thousands of grids still point at. The next
    /// physics step to touch one of them jumps through the freed shape's vtable: the stand's access violations in a
    /// physics job at Havok+0xa2cfd8 (30.09.2026), three times after load_test_500 (500 ships, 1.4 million blocks;
    /// AtmosphericThrusterSmall had 24493 references at 128 ships) and its cleanup. One of the dumps had the box shape
    /// referenced 91460 times with a count of -3.
    ///
    /// Havok skips the counting of an object whose memory-size half-word (the high 16 bits of that word) is 0, as it does
    /// for objects that live in a loaded file: add and remove do nothing, the object is never deleted. After a model
    /// loads its collision, that half-word is cleared on its shapes and on the children a grid wraps. Models are
    /// only unloaded with the game (MyModels.UnloadData), so nothing is kept longer than a process of the server.
    /// </summary>
    [PatchShim]
    public static class ModelShapeRefcount
    {
        /// <summary>hkReferencedObject: vtable, then the reference count (16 bits) and the memory size and flags (16 bits).</summary>
        private const int MemSizeAndFlagsOffset = 0xA;

        private static readonly FieldInfo Handle = typeof(HkShape).GetField("m_handle", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ModelShapeRefcount", ctx, c =>
        {
            if (Handle == null) throw new MissingFieldException("HkShape.m_handle");
            var loadData = typeof(MyModel).GetMethod(nameof(MyModel.LoadData), BindingFlags.Instance | BindingFlags.Public)
                           ?? throw new MissingMethodException("MyModel.LoadData");
            c.GetPattern(loadData).Suffixes.Add(typeof(ModelShapeRefcount).GetMethod(nameof(LoadDataSuffix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static void LoadDataSuffix(MyModel __instance)
        {
            var shapes = __instance.HavokCollisionShapes;
            if (shapes == null) return;
            foreach (var shape in shapes)
            {
                if (shape.IsZero) continue;
                StopCounting(shape);
                // The children MyCubeBlockCollector.AddShapesCustom wraps one by one.
                if (shape.ShapeType == HkShapeType.List)
                {
                    var list = (HkListShape)shape;
                    for (var i = 0; i < list.TotalChildrenCount; i++)
                        StopCounting(list.GetChildByIndex(i));
                }
                else if (shape.ShapeType == HkShapeType.Mopp)
                {
                    var collection = ((HkMoppBvTreeShape)shape).ShapeCollection;
                    for (var i = 0; i < collection.ShapeCount; i++)
                        StopCounting(collection.GetShape((uint)i, null));
                }
            }
        }

        private static void StopCounting(HkShape shape)
        {
            if (shape.IsZero) return;
            var native = (IntPtr)Handle.GetValue(shape);
            if (native != IntPtr.Zero) Marshal.WriteInt16(native, MemSizeAndFlagsOffset, 0);
        }
    }
}
