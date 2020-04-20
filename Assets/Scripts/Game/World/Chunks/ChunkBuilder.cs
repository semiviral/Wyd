#region

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Serilog;
using Unity.Mathematics;
using UnityEngine;
using Wyd.Controllers.State;
using Wyd.Controllers.System;
using Wyd.Controllers.World;
using Wyd.Controllers.World.Chunk;
using Wyd.System;
using Wyd.System.Collections;
using Wyd.System.Jobs;
using Random = System.Random;

#endregion

namespace Wyd.Game.World.Chunks
{
    public class ChunkBuilder
    {
        private static readonly ObjectCache<float[]> _noiseValuesCache = new ObjectCache<float[]>();

        private readonly Random _SeededRandom;
        private readonly Stopwatch _Stopwatch;
        private readonly Dictionary<string, ushort> _BlockIDCache;
        private readonly float3 _OriginPoint;
        private readonly ComputeBuffer _NoiseValuesBuffer;
        private readonly float _Frequency;
        private readonly float _Persistence;
        private readonly CancellationToken _CancellationToken;

        private OctreeNode _Blocks;
        private bool _GpuAcceleration;
        private float[] _NoiseMap;

        public TimeSpan NoiseRetrievalTimeSpan { get; private set; }
        public TimeSpan TerrainGenerationTimeSpan { get; private set; }

        public ChunkBuilder(CancellationToken cancellationToken, float3 originPoint, float frequency,
            float persistence, bool gpuAcceleration = false, ComputeBuffer noiseValuesBuffer = null)
        {
            _BlockIDCache = new Dictionary<string, ushort>();
            _SeededRandom = new Random(WorldController.Current.Seed);
            _Stopwatch = new Stopwatch();
            _OriginPoint = originPoint;
            _Frequency = frequency;
            _Persistence = persistence;
            _GpuAcceleration = gpuAcceleration;
            _NoiseValuesBuffer = noiseValuesBuffer;
            _CancellationToken = cancellationToken;
        }

        public void GetGeneratedBlockData(out OctreeNode blocks)
        {
            blocks = _Blocks;
        }


        #region Generation Execution

        private ushort GetCachedBlockID(string blockName)
        {
            if (_BlockIDCache.TryGetValue(blockName, out ushort id))
            {
                return id;
            }
            else if (BlockController.Current.TryGetBlockId(blockName, out id))
            {
                _BlockIDCache.Add(blockName, id);
                return id;
            }

            return BlockController.AirID;
        }

        private void GetComputeBufferData()
        {
            _Stopwatch.Restart();

            _NoiseValuesBuffer?.GetData(_NoiseMap);
            _NoiseValuesBuffer?.Release();

            _Stopwatch.Stop();
        }

        public void TimeMeasuredGenerate()
        {
            _Stopwatch.Restart();

            GenerateNoise();

            _Stopwatch.Stop();

            NoiseRetrievalTimeSpan = _Stopwatch.Elapsed;

            _Stopwatch.Restart();

            Generate();

            _noiseValuesCache.CacheItem(ref _NoiseMap);

            _Stopwatch.Stop();

            TerrainGenerationTimeSpan = _Stopwatch.Elapsed;
        }

        private void GenerateNoise()
        {
            _NoiseMap = _noiseValuesCache.Retrieve() ?? new float[WydMath.Product(ChunkController.SizeCubed)];

            if (_GpuAcceleration && (_NoiseValuesBuffer != null))
            {
                using (ManualResetEvent manualResetEvent = new ManualResetEvent(false))
                {
                    MainThreadActionsController.Current.PushAction(new MainThreadAction(manualResetEvent,
                        GetComputeBufferData));
                    manualResetEvent.WaitOne();
                }
            }
            else if (_GpuAcceleration && (_NoiseValuesBuffer == null))
            {
                Log.Warning(
                    $"`{nameof(_GpuAcceleration)}` is set to true, but no noise values were provided. Defaulting to CPU-bound generation.");
                _GpuAcceleration = false;
            }
            else
            {
                for (int index = 0; index < WydMath.Product(_Blocks.Volume.Size); index++)
                {
                    if (_CancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    _NoiseMap[index] =
                        GetNoiseValueByGlobalPosition(_Blocks.Volume.MinPoint
                                                      + WydMath.IndexTo3D(index, ChunkController.SizeCubed));
                }

                _Stopwatch.Stop();
            }

            NoiseRetrievalTimeSpan = _Stopwatch.Elapsed;
        }

        private void Generate()
        {
            int sizeProduct = WydMath.Product(ChunkController.SizeCubed);
            bool allAir = true;
            bool allStone = true;

            for (int index = 0; index < sizeProduct; index++)
            {
                if (_CancellationToken.IsCancellationRequested)
                {
                    return;
                }
                else if (!allAir && !allStone)
                {
                    break;
                }

                if (_NoiseMap[index] >= 0.01f)
                {
                    allAir = false;
                }

                if (_NoiseMap[index] < 0.011f)
                {
                    allStone = false;
                }
            }

            float3 volumeCenterPoint = _OriginPoint + ChunkController.SizeExtents;

            if (allStone)
            {
                _Blocks = new OctreeNode(volumeCenterPoint, ChunkController.SIZE, GetCachedBlockID("stone"));
                return;
            }
            else
            {
                _Blocks = new OctreeNode(volumeCenterPoint, ChunkController.SIZE, BlockController.AirID);

                if (allAir)
                {
                    return;
                }
            }

            for (int index = WydMath.Product(ChunkController.SizeCubed) - 1; index >= 0; index--)
            {
                if (_CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (_NoiseMap[index] < 0.01f)
                {
                    continue; // air
                }

                float3 globalPosition = _Blocks.Volume.MinPoint + WydMath.IndexTo3D(index, ChunkController.SizeCubed);
                _Blocks.UncheckedSetPoint(globalPosition, GetBlockIDAtPosition(globalPosition, index));
            }
        }

        private ushort GetBlockIDAtPosition(float3 globalPosition, int index)
        {
            if ((globalPosition.y < 4) && (globalPosition.y <= _SeededRandom.Next(0, 4)))
            {
                return GetCachedBlockID("bedrock");
            }

            // TERRAIN GEN NOTES ON NOISE RANGES
            // Between: 0.0110f to 0.01f = surface crust
            // Between: 0.0105f to 0.01f = grass layer
            // Follows: 0.0105f to 0.0110f = dirt layer

            if (_NoiseMap[index] < 0.0105f)
            {
                return GetCachedBlockID("grass");
            }
            else if (_NoiseMap[index] < 0.011f)
            {
                return GetCachedBlockID("dirt");
            }
            else
            {
                return GetCachedBlockID("stone");
            }
        }

        private float GetNoiseValueByGlobalPosition(float3 globalPosition)
        {
            float noiseValue = OpenSimplexSlim.GetSimplex(WorldController.Current.Seed, _Frequency, globalPosition);
            noiseValue += 5f * (1f - Mathf.InverseLerp(0f, WorldController.WORLD_HEIGHT, globalPosition.y));
            noiseValue /= globalPosition.y + (-1f * _Persistence);

            return noiseValue;
        }

        #endregion
    }
}
