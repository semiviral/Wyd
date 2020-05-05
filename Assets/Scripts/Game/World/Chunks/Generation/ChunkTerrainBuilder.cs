#region

using System;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;
using Wyd.Controllers.State;
using Wyd.Controllers.System;
using Wyd.Controllers.World;
using Wyd.System;
using Wyd.System.Collections;

#endregion

namespace Wyd.Game.World.Chunks.Generation
{
    public class ChunkTerrainBuilder : ChunkBuilder
    {
        private static readonly ObjectPool<float[]> _heightmapPool = new ObjectPool<float[]>();
        private static readonly ObjectPool<float[]> _caveNoisePool = new ObjectPool<float[]>();

        private readonly ComputeBuffer _HeightmapBuffer;
        private readonly ComputeBuffer _CaveNoiseBuffer;
        private readonly float _Frequency;
        private readonly float _Persistence;
        private readonly int _NoiseSeedA;
        private readonly int _NoiseSeedB;

        private float[] _Heightmap;
        private float[] _CaveNoise;

        public TimeSpan NoiseRetrievalTimeSpan { get; private set; }
        public TimeSpan TerrainGenerationTimeSpan { get; private set; }

        public ChunkTerrainBuilder(CancellationToken cancellationToken, int3 originPoint, float frequency, float persistence,
            ComputeBuffer heightmapBuffer = null, ComputeBuffer caveNoiseBuffer = null) : base(cancellationToken, originPoint)
        {
            _Frequency = frequency;
            _Persistence = persistence;
            _HeightmapBuffer = heightmapBuffer;
            _CaveNoiseBuffer = caveNoiseBuffer;

            _NoiseSeedA = WorldController.Current.Seed ^ 2;
            _NoiseSeedB = WorldController.Current.Seed ^ 3;
        }

        public override void Dispose()
        {
            base.Dispose();

            Array.Clear(_Heightmap, 0, _Heightmap.Length);
            Array.Clear(_CaveNoise, 0, _CaveNoise.Length);

            _heightmapPool.TryAdd(_Heightmap);
            _caveNoisePool.TryAdd(_CaveNoise);

            _Heightmap = null;
            _CaveNoise = null;
        }


        #region Terrain Generation

        private bool GetComputeBufferData()
        {
            _HeightmapBuffer?.GetData(_Heightmap);
            _CaveNoiseBuffer?.GetData(_CaveNoise);

            _HeightmapBuffer?.Dispose();
            _CaveNoiseBuffer?.Dispose();

            return true;
        }

        public void TimeMeasuredGenerate()
        {
            Stopwatch.Restart();

            GenerateNoise();

            Stopwatch.Stop();

            NoiseRetrievalTimeSpan = Stopwatch.Elapsed;

            Stopwatch.Restart();

            Generate();

            Stopwatch.Stop();

            TerrainGenerationTimeSpan = Stopwatch.Elapsed;
        }

        private void GenerateNoise()
        {
            _Heightmap = _heightmapPool.Retrieve() ?? new float[ChunkController.SIZE_SQUARED];
            _CaveNoise = _caveNoisePool.Retrieve() ?? new float[ChunkController.SIZE_CUBED];

            if ((_HeightmapBuffer == null) || (_CaveNoiseBuffer == null))
            {
                for (int x = 0; x < ChunkController.SIZE; x++)
                for (int z = 0; z < ChunkController.SIZE; z++)
                {
                    int2 xzCoords = new int2(x, z);
                    int heightmapIndex = WydMath.PointToIndex(xzCoords, ChunkController.SIZE);
                    _Heightmap[heightmapIndex] = GetNoiseValueByGlobalPosition(OriginPoint.xz + xzCoords);

                    for (int y = 0; y < ChunkController.SIZE; y++)
                    {
                        int3 localPosition = new int3(x, y, z);
                        int3 globalPosition = OriginPoint + localPosition;
                        int caveNoiseIndex = WydMath.PointToIndex(localPosition, ChunkController.SIZE);

                        _CaveNoise[caveNoiseIndex] = GetCaveNoiseByGlobalPosition(globalPosition);
                    }
                }
            }
            else
            {
                using (ManualResetEvent manualResetEvent = MainThreadActionsController.Current.QueueAction(GetComputeBufferData))
                {
                    manualResetEvent.WaitOne();
                }
            }
        }

        private void Generate()
        {
            _Blocks = new Octree(ChunkController.SIZE, BlockController.AirID);

            for (int x = 0; x < ChunkController.SIZE; x++)
            for (int z = 0; z < ChunkController.SIZE; z++)
            {
                if (CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                int heightmapIndex = WydMath.PointToIndex(new int2(x, z), ChunkController.SIZE);
                float noiseHeight = _Heightmap[heightmapIndex];

                if (noiseHeight < OriginPoint.y)
                {
                    continue;
                }

                int noiseHeightClamped = math.clamp((int)math.floor(noiseHeight - OriginPoint.y), 0, ChunkController.SIZE_MINUS_ONE);

                for (int y = noiseHeightClamped; y >= 0; y--)
                {
                    int3 localPosition = new int3(x, y, z);
                    int3 globalPosition = OriginPoint + localPosition;
                    int positionAsIndex = WydMath.PointToIndex(localPosition, ChunkController.SIZE);

                    if ((globalPosition.y < 4) && (globalPosition.y <= SeededRandom.Next(0, 4)))
                    {
                        _Blocks.SetPoint(localPosition, GetCachedBlockID("bedrock"));
                        continue;
                    }
                    else if (_CaveNoise[positionAsIndex] < 0.000225f)
                    {
                        continue;
                    }

                    int noiseHeightInt = (int)noiseHeight;

                    if (globalPosition.y == noiseHeightInt)
                    {
                        _Blocks.SetPoint(localPosition, GetCachedBlockID("grass"));
                    }
                    else if ((globalPosition.y < noiseHeightInt) && (globalPosition.y >= (noiseHeightInt - 3)))
                    {
                        _Blocks.SetPoint(localPosition, SeededRandom.Next(0, 8) == 0
                            ? GetCachedBlockID("dirt_coarse")
                            : GetCachedBlockID("dirt"));
                    }
                    else if (SeededRandom.Next(0, 100) == 0)
                    {
                        _Blocks.SetPoint(localPosition, GetCachedBlockID("coal_ore"));
                    }
                    else
                    {
                        _Blocks.SetPoint(localPosition, GetCachedBlockID("stone"));
                    }
                }
            }
        }

        private float GetCaveNoiseByGlobalPosition(int3 globalPosition)
        {
            float currentHeight = (globalPosition.y + (((WorldController.WORLD_HEIGHT / 4f) - (globalPosition.y * 1.25f)) * _Persistence)) * 0.85f;
            float heightDampener = math.unlerp(0f, WorldController.WORLD_HEIGHT, currentHeight);
            float noiseA = OpenSimplexSlim.GetSimplex(_NoiseSeedA, 0.01f, globalPosition) * heightDampener;
            float noiseB = OpenSimplexSlim.GetSimplex(_NoiseSeedB, 0.01f, globalPosition) * heightDampener;
            float noiseAPow2 = math.pow(noiseA, 2f);
            float noiseBPow2 = math.pow(noiseB, 2f);

            return (noiseAPow2 + noiseBPow2) / 2f;
        }

        private float GetNoiseValueByGlobalPosition(int2 globalPosition)
        {
            float noise = OpenSimplexSlim.GetSimplex(WorldController.Current.Seed, _Frequency, globalPosition);
            float noiseAsWorldHeight = math.unlerp(-1f, 1f, noise) * WorldController.WORLD_HEIGHT;
            float noisePersistedWorldHeight =
                noiseAsWorldHeight + (((WorldController.WORLD_HEIGHT / 2f) - (noiseAsWorldHeight * 1.25f)) * _Persistence);

            return noisePersistedWorldHeight;
        }

        #endregion
    }
}