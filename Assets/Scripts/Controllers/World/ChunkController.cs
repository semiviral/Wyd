#region

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Controllers.Entity;
using Controllers.Game;
using Environment.Terrain;
using Static;
using UnityEngine;

#endregion

namespace Controllers.World
{
    public sealed class ChunkController : MonoBehaviour
    {
        public static ChunkController Current;

        private Stopwatch _FrameTimeLimiter;
        private Chunk _ChunkObject;
        private List<Chunk> _Chunks;
        private Queue<Chunk> _CachedChunks;
        private Queue<Chunk> _DeactivationQueue;

        public Queue<Vector3Int> BuildChunkQueue;
        public int ActiveChunksCount => _Chunks.Count;
        public int CachedChunksCount => _CachedChunks.Count;
        public bool AllChunksBuilt => _Chunks.All(chunk => chunk.Built);
        public bool AllChunksMeshed => _Chunks.All(chunk => chunk.Generated);

        private void Awake()
        {
            if ((Current != null) && (Current != this))
            {
                Destroy(gameObject);
            }
            else
            {
                Current = this;
            }

            _ChunkObject = Resources.Load<Chunk>(@"Environment\Terrain\Chunk");
            _CachedChunks = new Queue<Chunk>();
            _DeactivationQueue = new Queue<Chunk>();
            _FrameTimeLimiter = new Stopwatch();

            _Chunks = new List<Chunk>();
            BuildChunkQueue = new Queue<Vector3Int>();
        }

        private void Start()
        {
            PlayerController.Current.ChunkChanged += MarkOutOfBoundsChunksForDeactivation;
        }

        private void Update()
        {
            _FrameTimeLimiter.Restart();

            if (BuildChunkQueue.Count > 0)
            {
                ProcessBuildChunkQueue();
            }

            if (_DeactivationQueue.Count > 0)
            {
                ProcessDeactivationQueue();
            }

            // if maximum chunk cache size is not zero...
            // cull chunks down to half the maximum when idle
            if ((OptionsController.Current.MaximumChunkCacheSize != 0) &&
                (_CachedChunks.Count > (OptionsController.Current.MaximumChunkCacheSize / 2)))
            {
                CullCachedChunks();
            }

            _FrameTimeLimiter.Stop();
        }

        #region CHUNK BUILDING

        public void ProcessBuildChunkQueue()
        {
            while (BuildChunkQueue.Count > 0)
            {
                Vector3Int position = BuildChunkQueue.Dequeue();

                if (CheckChunkExistsAtPosition(position))
                {
                    continue;
                }

                Chunk chunk;

                if (_CachedChunks.Count > 0)
                {
                    chunk = _CachedChunks.Dequeue();
                    chunk.Activate(position);
                }
                else
                {
                    chunk = Instantiate(_ChunkObject, position, Quaternion.identity, transform);
                }

                _Chunks.Add(chunk);

                // ensures that neighbours update their meshes to cull newly out of sight faces
                FlagNeighborsPendingUpdate(chunk.Position);

                if (_FrameTimeLimiter.Elapsed.TotalSeconds > OptionsController.Current.MaximumInternalFrames)
                {
                    break;
                }
            }
        }

        private void FlagNeighborsPendingUpdate(Vector3Int position)
        {
            for (int x = -1; x <= 1; x++)
            {
                if (x == 0)
                {
                    continue;
                }

                Vector3Int modifiedPosition = position + new Vector3Int(x * Chunk.Size.x, 0, 0);
                Chunk chunkAtPosition = GetChunkAtPosition(modifiedPosition);

                if ((chunkAtPosition == default) || chunkAtPosition.PendingMeshUpdate || !chunkAtPosition.Active)
                {
                    continue;
                }

                chunkAtPosition.PendingMeshUpdate = true;
            }

            for (int z = -1; z <= 1; z++)
            {
                if (z == 0)
                {
                    continue;
                }

                Vector3Int modifiedPosition = position + new Vector3Int(0, 0, z * Chunk.Size.z);
                Chunk chunkAtPosition = GetChunkAtPosition(modifiedPosition);

                if ((chunkAtPosition == default) || chunkAtPosition.PendingMeshUpdate || !chunkAtPosition.Active)
                {
                    continue;
                }

                chunkAtPosition.PendingMeshUpdate = true;
            }
        }

        #endregion


        #region CHUNK DISABLING

        private void MarkOutOfBoundsChunksForDeactivation(object sender, Vector3Int chunkPosition)
        {
            for (int i = _Chunks.Count - 1; i >= 0; i--)
            {
                Vector3Int difference = (_Chunks[i].Position - chunkPosition).Abs();

                if ((difference.x <= ((WorldController.Current.WorldGenerationSettings.Radius + 1) * Chunk.Size.x)) &&
                    (difference.z <= ((WorldController.Current.WorldGenerationSettings.Radius + 1) * Chunk.Size.z)))
                {
                    continue;
                }

                _DeactivationQueue.Enqueue(_Chunks[i]);
            }
        }

        private void ProcessDeactivationQueue()
        {
            while (_DeactivationQueue.Count > 0)
            {
                Chunk chunk = _DeactivationQueue.Dequeue();
                DeactivateChunk(chunk);

                if (_FrameTimeLimiter.Elapsed.TotalSeconds > OptionsController.Current.MaximumInternalFrames)
                {
                    break;
                }
            }
        }

        private void DeactivateChunk(Chunk chunk)
        {
            _CachedChunks.Enqueue(chunk);
            _Chunks.Remove(chunk);
            FlagNeighborsPendingUpdate(chunk.Position);
            chunk.Deactivate();
        }

        private void CullCachedChunks()
        {
            if (OptionsController.Current.MaximumChunkCacheSize == 0)
            {
                return;
            }

            // controller will cull chunks down to half the maximum when idle
            while (_CachedChunks.Count > (OptionsController.Current.MaximumChunkCacheSize / 2))
            {
                Chunk chunk = _CachedChunks.Dequeue();
                Destroy(chunk.gameObject);

                // continue culling if the amount of cached chunks is greater than the maximum
                if ((_FrameTimeLimiter.Elapsed.TotalSeconds >
                     OptionsController.Current.MaximumInternalFrames) &&
                    (OptionsController.Current.ChunkCacheCullingAggression == CacheCullingAggression.Passive))
                {
                    return;
                }
            }
        }

        #endregion


        #region MISC

        public bool CheckChunkExistsAtPosition(Vector3Int position)
        {
            // reverse for loop to avoid collection modified from thread errors
            for (int i = _Chunks.Count - 1; i >= 0; i--)
            {
                if ((_Chunks.Count <= i) || (_Chunks[i] == default))
                {
                    continue;
                }

                if (_Chunks[i].Position == position)
                {
                    return true;
                }
            }

            return false;
        }

        public Chunk GetChunkAtPosition(Vector3Int position)
        {
            // reverse for loop to avoid collection modified from thread errors
            for (int i = _Chunks.Count - 1; i >= 0; i--)
            {
                if ((_Chunks.Count <= i) || (_Chunks[i] == default))
                {
                    continue;
                }

                if (_Chunks[i].Position == position)
                {
                    return _Chunks[i];
                }
            }

            return default;
        }

        public bool TryGetBlockAtPosition(Vector3Int position, out Block block)
        {
            Vector3Int chunkPosition = WorldController.GetWorldChunkOriginFromGlobalPosition(position).ToInt();

            Chunk chunk = GetChunkAtPosition(chunkPosition);

            if ((chunk == default) || !chunk.Built)
            {
                return false;
            }

            Vector3Int localPosition = (position - chunkPosition).Abs();
            int localPosition1d =
                localPosition.x + (Chunk.Size.x * (localPosition.y + (Chunk.Size.y * localPosition.z)));

            if (chunk.Blocks.Length <= localPosition1d)
            {
                return false;
            }

            block = chunk.Blocks[localPosition1d];

            return true;
        }

        #endregion
    }
}