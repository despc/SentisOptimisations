using System;
using System.Diagnostics;
using NLog;
using VRage.Game;
using VRage.Library.Collections;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Serialization;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The serializers the first joining player would otherwise build are made while the world loads.
    ///
    /// Handing a replicable to a client writes the entity into the creation packet with
    /// <c>MySerializer.Write(..., MyObjectBuilderSerializerKeen.Dynamic)</c>, and the very first write
    /// of a type builds its serializer. Measured on the stand: the first character sent to the first
    /// client cost <b>66 ms</b> inside the frame, while every later one cost 0.03 ms - the same
    /// warm-up is paid for grids and floating objects. It is a one-off, but it lands squarely on the
    /// frame in which somebody joins.
    ///
    /// So the same write is done here once at world load, into a stream that is thrown away, where
    /// nobody is watching the frame rate.
    /// </summary>
    public static class SerializerWarmup
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static bool _done;

        /// <summary>Milliseconds of serializer building kept out of the first join.</summary>
        public static double WarmedMs { get; private set; }

        public static void Run()
        {
            if (_done) return;
            _done = true;

            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                using (var stream = new BitStream())
                {
                    stream.ResetWrite();
                    Warm<MyObjectBuilder_Character>(stream);
                    Warm<MyObjectBuilder_CubeGrid>(stream);
                    Warm<MyObjectBuilder_FloatingObject>(stream);
                }

                WarmedMs = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
                Log.Info($"Object builder serializers warmed up in {WarmedMs:F0} ms; the first player to join no longer pays it");
            }
            catch (Exception e)
            {
                // Nothing is lost: the first join builds the serializer as it always did.
                Log.Warn(e, "Could not warm up the object builder serializers");
            }
        }

        private static void Warm<T>(BitStream stream) where T : MyObjectBuilder_Base, new()
        {
            try
            {
                var builder = MyObjectBuilderSerializerKeen.CreateNewObject<T>();
                if (builder == null) return;
                var asBase = (MyObjectBuilder_Base)builder;
                MySerializer.Write(stream, ref asBase, MyObjectBuilderSerializerKeen.Dynamic);
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not warm up the serializer of " + typeof(T).Name);
            }
        }

        /// <summary>Starts over for a new world.</summary>
        public static void Reset() => _done = false;
    }
}
