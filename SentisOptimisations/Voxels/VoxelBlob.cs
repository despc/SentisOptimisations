using Sandbox.Engine.Voxels;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The compressed copy of a voxel storage (what a connecting client is sent), made without holding the storage's
    /// lock while it is compressed.
    ///
    /// <c>MyStorageBase.Save</c> serializes and gzips the whole storage under the storage's shared lock, and on a planet
    /// that has been dug that takes hundreds of milliseconds - all the while a drill (a player's, a ship's) that cuts
    /// the ground waits on the game thread for the exclusive lock: a frame of 459 ms on the stand. The world save does
    /// it the other way: the raw bytes under the lock (<c>GetVoxelData</c>, fast, the octree's leaves on all cores with
    /// <see cref="ParallelVoxelSave"/>), the compression after, and the result handed back with <c>SetDataCache</c>,
    /// which the storage takes only if nothing changed it in between. The same here.
    /// </summary>
    public static class VoxelBlob
    {
        /// <summary>The storage's compressed data: the cached copy when there is one, else made (worker thread).</summary>
        public static byte[] Get(MyStorageBase storage)
        {
            if (storage.AreDataCached && storage.AreDataCachedCompressed)
            {
                storage.Save(out var cached);
                return cached;
            }
            // the raw bytes into a kept buffer (VoxelBuffers), not a new array of the storage's size
            var raw = VoxelBuffers.Rent();
            try
            {
                VoxelBuffers.CaptureRaw(storage, raw);
                byte[] compressed;
                if (raw.Length > 0) compressed = VoxelBuffers.Gzip(raw.GetBuffer(), (int)raw.Length);
                else
                {
                    // not captured (a storage VoxelBuffers leaves to the game): the game's array
                    var bytes = storage.GetVoxelData();
                    compressed = VoxelBuffers.Gzip(bytes, bytes.Length);
                }
                storage.SetDataCache(compressed, true);
                return compressed;
            }
            finally { VoxelBuffers.Return(raw); }
        }
    }
}
