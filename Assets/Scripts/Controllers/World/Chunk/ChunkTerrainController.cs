#region

using System;
using System.Linq;
using UnityEngine;
using Wyd.Controllers.State;
using Wyd.Game;
using Wyd.Game.World.Chunks.BuildingJob;
using Wyd.Game.World.Chunks.Events;
using Wyd.System;
using Wyd.System.Collections;
using Wyd.System.Extensions;
using Wyd.System.Jobs;

#endregion

namespace Wyd.Controllers.World.Chunk
{
    public class ChunkTerrainController : ActivationStateChunkController
    {
        private static readonly ObjectCache<ChunkBuildingJobRawTerrain> _ChunkRawTerrainBuilderCache =
            new ObjectCache<ChunkBuildingJobRawTerrain>(true);

        private static readonly ObjectCache<ChunkBuildingJobAccents> _ChunkAccentsBuilderCache =
            new ObjectCache<ChunkBuildingJobAccents>(true);

        #region INSTANCE MEMBERS

        private TimeSpan _AggregateBuildTime;
        private ComputeShader _NoiseShader;

        private object _JobIdentity;

        public GenerationData.GenerationStep CurrentStep { get; private set; }
        public bool Generating { get; private set; }

        #endregion

        #region SERIALIZED MEMBERS

        [SerializeField]
        private ChunkBlocksController BlocksController;

        [SerializeField]
        [ReadOnlyInspectorField]
        private int TotalTimesTerrainChanged;

        [SerializeField]
        [ReadOnlyInspectorField]
        private int TimesTerrainChanged;

        #endregion

        protected override void Awake()
        {
            base.Awake();

            _NoiseShader = GameController.LoadResource<ComputeShader>(@"Graphics\Shaders\NoiseComputationShader");
            _NoiseShader.SetInt("_NoiseSeed", WorldController.Current.Seed);
            _NoiseShader.SetVector("_MaximumSize",
                new Vector4(ChunkController.Size.x, ChunkController.Size.y,
                    ChunkController.Size.z, 0f));
        }

        private void Update()
        {
            // if we've passed safe frame time for target
            // fps, then skip updates as necessary to reach
            // next frame
            if (!WorldController.Current.IsInSafeFrameTime())
            {
                return;
            }

            if (Generating
                || (CurrentStep == GenerationData.GenerationStep.Complete)
                || (WorldController.Current.AggregateNeighborsStep(_Position) < CurrentStep))
            {
                return;
            }

            ExecuteStep(CurrentStep);
        }


        #region DE/ACTIVATION

        public override void Activate(Vector3 position, bool setPosition)
        {
            base.Activate(position, setPosition);
            ClearInternalData();
        }

        public override void Deactivate()
        {
            base.Deactivate();
            ClearInternalData();
        }

        private void ClearInternalData()
        {
            TimesTerrainChanged = 0;
            _AggregateBuildTime = TimeSpan.Zero;
            _JobIdentity = null;
            CurrentStep = GenerationData.GenerationStep.Accents;
            Generating = false;
        }

        #endregion

        #region RUNTIME

        private void QueueJob(Job job)
        {
            if (!GameController.Current.TryQueueJob(job, out _JobIdentity))
            {
                return;
            }

            GameController.Current.JobFinished += OnJobFinished;
            Generating = true;
        }

        private void ExecuteStep(GenerationData.GenerationStep step)
        {
            switch (step)
            {
                case GenerationData.GenerationStep.RawTerrain:
                    BeginGeneratingRawTerrain();
                    break;
                case GenerationData.GenerationStep.Accents:
                    BeginGeneratingAccents();
                    break;
                case GenerationData.GenerationStep.Complete:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(step), step, null);
            }
        }

        private void BeginGeneratingRawTerrain()
        {
            const float frequency = 0.01f;
            const float persistence = -1f;

            if (Generating)
            {
                return;
            }

            ChunkBuildingJobRawTerrain job = _ChunkRawTerrainBuilderCache.RetrieveItem()
                                             ?? new ChunkBuildingJobRawTerrain();

            if (OptionsController.Current.GPUAcceleration)
            {
                ComputeBuffer noiseBuffer = new ComputeBuffer(ChunkController.Size.Product(), 4);
                int kernel = _NoiseShader.FindKernel("CSMain");
                _NoiseShader.SetVector("_Offset", _Position);
                _NoiseShader.SetFloat("_Frequency", frequency);
                _NoiseShader.SetFloat("_Persistence", persistence);
                _NoiseShader.SetBuffer(kernel, "Result", noiseBuffer);
                // 1024 is the value set in the shader's [numthreads(--> 1024 <--, 1, 1)]
                _NoiseShader.Dispatch(kernel, ChunkController.Size.Product() / 1024, 1, 1);

                job.SetData(new GenerationData(_Bounds, BlocksController.Blocks), frequency, persistence,
                    OptionsController.Current.GPUAcceleration, noiseBuffer);
            }
            else
            {
                job.SetData(new GenerationData(_Bounds, BlocksController.Blocks), frequency, persistence);
            }

            QueueJob(job);
        }

        // todo fix this
        public void BeginGeneratingAccents()
        {
            if (Generating)
            {
                return;
            }

            ChunkBuildingJobAccents job = _ChunkAccentsBuilderCache.RetrieveItem() ?? new ChunkBuildingJobAccents();

            job.SetGenerationData(new GenerationData(_Bounds, BlocksController.Blocks));

            QueueJob(job);
        }

        #endregion


        #region EVENTS

        public event ChunkChangedEventHandler TerrainChanged;

        private void OnChunkTerrainChanged(object sender, ChunkChangedEventArgs args)
        {
            TerrainChanged?.Invoke(sender, args);
        }

        private void OnJobFinished(object sender, JobEventArgs args)
        {
            if (args.Job.Identity != _JobIdentity)
            {
                return;
            }

            switch (CurrentStep)
            {
                case GenerationData.GenerationStep.RawTerrain:
                    _AggregateBuildTime += args.Job.ExecutionTime;
                    OnChunkTerrainChanged(this,
                        new ChunkChangedEventArgs(_Bounds, Directions.CardinalDirectionsVector3));
                    break;
                case GenerationData.GenerationStep.Accents:
                    _AggregateBuildTime += args.Job.ExecutionTime;
                    OnChunkTerrainChanged(this, new ChunkChangedEventArgs(_Bounds, Enumerable.Empty<Vector3>()));
                    break;
                case GenerationData.GenerationStep.Complete:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            // this check always BEFORE incrementing the step
            if (CurrentStep == GenerationData.FINAL_TERRAIN_STEP)
            {
                DiagnosticsController.Current.RollingChunkBuildTimes.Enqueue(_AggregateBuildTime);
                _AggregateBuildTime = TimeSpan.Zero;

                TotalTimesTerrainChanged += 1;
                TimesTerrainChanged += 1;
            }

            CurrentStep = CurrentStep.Next();
            Generating = false;
            _JobIdentity = null;
            GameController.Current.JobFinished -= OnJobFinished;
        }

        #endregion
    }
}
