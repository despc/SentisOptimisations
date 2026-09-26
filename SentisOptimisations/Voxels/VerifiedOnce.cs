using System;
using System.IO;
using System.Linq;
using Sandbox.Engine.Voxels;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Which of the plugin's own ways of writing voxel data have been checked against the game's, for this build of
    /// the plugin and this build of the game: a check writes a whole planet twice while holding its lock, and a drill
    /// waiting for that lock stopped the game thread for 240 ms - after every restart. Checked once per build pair
    /// (a new plugin or a game update checks again), remembered in a file next to the config.
    /// </summary>
    internal static class VerifiedOnce
    {
        private const string FileName = "SentisOptimisations.verified.txt";
        private static readonly object Lock = new object();

        private static string Key(string what) =>
            what + " " + typeof(MyOctreeStorage).Assembly.ManifestModule.ModuleVersionId + " " + typeof(VerifiedOnce).Assembly.ManifestModule.ModuleVersionId;

        private static string PathOf() =>
            SentisOptimisationsPlugin.Instance?.StoragePath is string folder ? Path.Combine(folder, FileName) : null;

        public static bool Is(string what)
        {
            try
            {
                lock (Lock)
                {
                    var path = PathOf();
                    return path != null && File.Exists(path) && File.ReadAllLines(path).Contains(Key(what));
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Mark(string what)
        {
            try
            {
                lock (Lock)
                {
                    var path = PathOf();
                    if (path == null) return;
                    // the lines of other builds are dropped: only the current pair counts
                    var ours = typeof(VerifiedOnce).Assembly.ManifestModule.ModuleVersionId.ToString();
                    var game = typeof(MyOctreeStorage).Assembly.ManifestModule.ModuleVersionId.ToString();
                    var lines = File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Contains(ours) && l.Contains(game)).ToList() : new System.Collections.Generic.List<string>();
                    if (!lines.Contains(Key(what))) lines.Add(Key(what));
                    File.WriteAllLines(path, lines);
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "Could not note a checked voxel write");
            }
        }
    }
}
