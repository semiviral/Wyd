#region

using System.Threading;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using Wyd.Controllers.System;

#endregion

namespace Wyd.Game.World.Chunks
{
    public class ChunkTerrainTerrainJob : ChunkTerrainJob
    {
        private readonly float _Frequency;
        private readonly float _Persistence;
        private readonly bool _GpuAcceleration;

        public ComputeBuffer NoiseValuesBuffer { get; }

        public ChunkTerrainTerrainJob(CancellationToken cancellationToken, float3 originPoint, float frequency,
            float persistence, bool gpuAcceleration = false, ComputeBuffer noiseValuesBuffer = null)
            : base(cancellationToken, originPoint)
        {
            _Frequency = frequency;
            _Persistence = persistence;
            _GpuAcceleration = gpuAcceleration;
            NoiseValuesBuffer = noiseValuesBuffer;
        }

        protected override Task Process()
        {
            ChunkTerrainBuilder builder = new ChunkTerrainBuilder(CancellationToken, OriginPoint, _Frequency,
                _Persistence, _GpuAcceleration, NoiseValuesBuffer);
            builder.TimeMeasuredGenerate();

            // builder has completed execution, so set field
            _TerrainOperator = builder;

            return Task.CompletedTask;
        }

        protected override Task ProcessFinished()
        {
            if (!CancellationToken.IsCancellationRequested)
            {
                ChunkTerrainBuilder builder = (ChunkTerrainBuilder)_TerrainOperator;

                DiagnosticsController.Current.RollingNoiseRetrievalTimes.Enqueue(builder.NoiseRetrievalTimeSpan);
                DiagnosticsController.Current.RollingTerrainBuildingTimes.Enqueue(builder.TerrainGenerationTimeSpan);
            }

            return Task.CompletedTask;
        }
    }
}