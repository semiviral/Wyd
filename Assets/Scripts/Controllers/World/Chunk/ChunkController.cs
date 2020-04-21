#region

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Serilog;
using Unity.Mathematics;
using UnityEngine;
using Wyd.Controllers.State;
using Wyd.Controllers.System;
using Wyd.Game;
using Wyd.Game.World.Blocks;
using Wyd.Game.World.Chunks;
using Wyd.System;
using Wyd.System.Collections;
using Wyd.System.Extensions;
using Wyd.System.Jobs;

#endregion

namespace Wyd.Controllers.World.Chunk
{
    public enum ChunkState
    {
        Terrain,
        AwaitingTerrain,
        Detail,
        AwaitingDetail,
        Mesh,
        AwaitingMesh,
        Meshed
    }

    public class ChunkController : ActivationStateChunkController, IPerFrameIncrementalUpdate
    {
        public const int SIZE = 32;
        public const int SIZE_CUBED = SIZE * SIZE * SIZE;

        private static readonly ObjectCache<BlockAction> _blockActionsCache = new ObjectCache<BlockAction>(true, 1024);

        public static readonly int3 Size3D = new int3(SIZE);
        public static readonly int3 Size3DExtents = new int3(SIZE / 2);


        #region Instance Members

        private CancellationTokenSource _CancellationTokenSource;
        private ConcurrentQueue<BlockAction> _BlockActions;
        private OctreeNode<ushort> _Blocks;
        private Mesh _Mesh;

        private long _State;
        private bool _Active;
        private object _ActiveLock;
        private bool _UpdateMesh;
        private object _UpdateMeshLock;
        private ChunkMeshData _PendingMeshData;

        public bool Active
        {
            get
            {
                bool tmp;

                lock (_ActiveLock)
                {
                    tmp = _Active;
                }

                return tmp;
            }
            set
            {
                lock (_ActiveLock)
                {
                    _Active = value;
                }
            }
        }

        public bool UpdateMesh
        {
            get
            {
                bool tmp;

                lock (_UpdateMeshLock)
                {
                    tmp = _UpdateMesh;
                }

                return tmp;
            }
            set
            {
                lock (_UpdateMeshLock)
                {
                    _UpdateMesh = value;
                }
            }
        }

        public OctreeNode<ushort> Blocks => _Blocks;

        public ChunkState State
        {
            get => (ChunkState)Interlocked.Read(ref _State);
            private set
            {
                Interlocked.Exchange(ref _State, (long)value);

#if UNITY_EDITOR

                BinaryState = Convert.ToString(unchecked((byte)State), 2).PadLeft(8, '0');

#endif
            }
        }

        #endregion


        #region Serialized Members

        [SerializeField]
        private ChunkTerrainController TerrainController;

        [SerializeField]
        private ChunkMeshController MeshController;

#if UNITY_EDITOR

        [SerializeField]
        [ReadOnlyInspectorField]
        private Vector3 MinimumPoint;

        [SerializeField]
        [ReadOnlyInspectorField]
        private Vector3 MaximumPoint;

        [SerializeField]
        [ReadOnlyInspectorField]
        private Vector3 Extents;

        [SerializeField]
        [ReadOnlyInspectorField]
        private string BinaryState;

        [SerializeField]
        private bool Regenerate;
#endif

        #endregion


        protected override void Awake()
        {
            base.Awake();

            _ActiveLock = new object();
            _UpdateMeshLock = new object();

            _BlockActions = new ConcurrentQueue<BlockAction>();
            _Mesh = new Mesh();

            void FlagUpdateMesh(object sender, ChunkChangedEventArgs args)
            {
                FlagMeshForUpdate();
            }

            BlocksChanged += FlagUpdateMesh;
            TerrainChanged += FlagUpdateMesh;
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            PerFrameUpdateController.Current.RegisterPerFrameUpdater(20, this);

            _CancellationTokenSource = new CancellationTokenSource();

            State = ChunkState.Terrain;

            Active = gameObject.activeSelf;

#if UNITY_EDITOR

            MinimumPoint = OriginPoint;
            MaximumPoint = OriginPoint + Size3D;
            Extents = WydMath.ToFloat(Size3D) / 2f;

#endif
        }

        protected override void OnDisable()
        {
            base.OnDisable();

            PerFrameUpdateController.Current.DeregisterPerFrameUpdater(20, this);

            if (_Mesh != null)
            {
                _Mesh.Clear();
            }

            _CancellationTokenSource.Cancel();
            _BlockActions = new ConcurrentQueue<BlockAction>();
            _Blocks = null;

            State = ChunkState.Terrain;

            Active = gameObject.activeSelf;
        }

        public void FrameUpdate()
        {
#if UNITY_EDITOR

            if (Regenerate)
            {
                State = ChunkState.Terrain;
                _PendingMeshData = null;
                Regenerate = false;
            }

#endif

            if (!WorldController.Current.ReadyForGeneration
                || WorldController.Current.GetNeighboringChunks(OriginPoint)
                    .Any(chunkController => chunkController.State < State))
            {
                return;
            }

            switch (State)
            {
                case ChunkState.Terrain:
                    _Blocks = null;
                    TerrainController.BeginTerrainGeneration(_CancellationTokenSource.Token,
                        OnTerrainGenerationFinished);
                    State = State.Next();
                    break;
                case ChunkState.AwaitingTerrain:
                    if (_Blocks != null)
                    {
                        State = State.Next();
                    }

                    break;
                case ChunkState.Detail:
                    TerrainController.BeginTerrainDetailing(_CancellationTokenSource.Token,
                        OnTerrainDetailingFinished, _Blocks);
                    _Blocks = null;

                    OnLocalTerrainChanged(this, new ChunkChangedEventArgs(OriginPoint,
                        Directions.AllDirectionNormals.Select(WydMath.ToFloat)));

                    State = State.Next();
                    break;
                case ChunkState.AwaitingDetail:
                    if (Blocks != null)
                    {
                        State = State.Next();
                    }

                    break;
                case ChunkState.Mesh:
                    if (Blocks.IsUniform
                        && ((Blocks.Value == BlockController.AirID)
                            || WorldController.Current.GetNeighboringChunks(OriginPoint)
                                .All(chunkController => chunkController.Blocks.IsUniform)))
                    {
                        State = ChunkState.Meshed;
                    }
                    else
                    {
                        MeshController.BeginGeneratingMesh(Blocks, _CancellationTokenSource.Token, OnMeshingFinished);
                        State = State.Next();
                    }

                    UpdateMesh = false;

                    break;
                case ChunkState.AwaitingMesh:
                    if (_PendingMeshData != null)
                    {
                        MeshController.ApplyMesh(_PendingMeshData);
                        _PendingMeshData = null;
                        State = State.Next();
                        OnMeshChanged(this, new ChunkChangedEventArgs(OriginPoint, Enumerable.Empty<float3>()));
                    }

                    break;
                case ChunkState.Meshed:
                    if (UpdateMesh)
                    {
                        State = ChunkState.Mesh;
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public IEnumerable IncrementalFrameUpdate()
        {
            if (State < ChunkState.Mesh)
            {
                yield break;
            }

            while (_BlockActions.TryDequeue(out BlockAction blockAction))
            {
                ProcessBlockAction(blockAction);

                _blockActionsCache.CacheItem(ref blockAction);

                yield return null;
            }
        }

        public void FlagMeshForUpdate()
        {
            if (!UpdateMesh)
            {
                UpdateMesh = true;
            }
        }


        #region De/Activation

        public void Activate(float3 position)
        {
            _SelfTransform.SetPositionAndRotation(position, quaternion.identity);

            gameObject.SetActive(true);
        }

        public void Deactivate()
        {
            gameObject.SetActive(false);
        }

        #endregion


        #region Block Actions

        private void ProcessBlockAction(BlockAction blockAction)
        {
            ModifyBlockPosition(blockAction.GlobalPosition, blockAction.Id);

            OnBlocksChanged(this,
                TryGetNeighborsRequiringUpdateNormals(blockAction.GlobalPosition, out IEnumerable<float3> normals)
                    ? new ChunkChangedEventArgs(OriginPoint, normals)
                    : new ChunkChangedEventArgs(OriginPoint, Enumerable.Empty<float3>()));
        }

        private void ModifyBlockPosition(float3 globalPosition, ushort newId)
        {
            Blocks.UncheckedSetPoint(globalPosition, newId);
        }

        private bool TryGetNeighborsRequiringUpdateNormals(float3 globalPosition, out IEnumerable<float3> normals)
        {
            normals = Enumerable.Empty<float3>();

            // set local position to center point of chunk
            float3 localPosition = globalPosition - (OriginPoint + Size3DExtents);

            // save signs of axes
            float3 localPositionSign = math.sign(localPosition);

            // add 0.5f to every axis (for case of pos 15f) and ceil every axis, then abs
            float3 localPositionAbs = math.abs(math.ceil(localPosition + (new float3(0.5f) * localPositionSign)));

            // if any axes are 16f, it means they were on the edge
            if (!math.any(localPositionAbs == 16f))
            {
                return false;
            }

            // divide by 16f & floor for 0 or 1 values, apply signs, and set component normals
            normals = WydMath.ToComponents(math.floor(localPositionAbs / 16f) * localPositionSign);
            return true;
        }

        public bool TryGetBlock(float3 globalPosition, out ushort blockId)
        {
            blockId = 0;

            if (Blocks == null)
            {
#if UNITY_EDITOR

                Log.Verbose(
                    $"'{nameof(Blocks)}' is null (origin: {OriginPoint}, state: {Convert.ToString((int)State, 2)}).");

#endif

                return false;
            }
            else if (!Blocks.TryGetPoint(globalPosition, out blockId))
            {
#if UNITY_EDITOR

                Log.Verbose($"Failed to get block data from point {globalPosition} in chunk at origin {OriginPoint}.");

#endif

                return false;
            }
            else
            {
                return true;
            }
        }

        public bool TryPlaceBlock(float3 globalPosition, ushort newBlockId) =>
            (Blocks != null)
            && Blocks.ContainsMinBiased(globalPosition)
            && TryGetBlock(globalPosition, out ushort blockId)
            && (blockId != newBlockId)
            && AllocateBlockAction(globalPosition, newBlockId);

        private bool AllocateBlockAction(float3 globalPosition, ushort id)
        {
            BlockAction blockAction = _blockActionsCache.Retrieve();
            blockAction.SetData(globalPosition, id);
            _BlockActions.Enqueue(blockAction);
            return true;
        }

        #endregion


        #region Events

        public event ChunkChangedEventHandler BlocksChanged;
        public event ChunkChangedEventHandler TerrainChanged;
        public event ChunkChangedEventHandler MeshChanged;


        private void OnBlocksChanged(object sender, ChunkChangedEventArgs args)
        {
            BlocksChanged?.Invoke(sender, args);
        }

        private void OnLocalTerrainChanged(object sender, ChunkChangedEventArgs args)
        {
            TerrainChanged?.Invoke(sender, args);
        }

        private void OnTerrainGenerationFinished(object sender, AsyncJobEventArgs args)
        {
            args.AsyncJob.WorkFinished -= OnTerrainGenerationFinished;

            if (!Active)
            {
                return;
            }

            ((ChunkBuilderJob)args.AsyncJob).GetGeneratedBlockData(out _Blocks);
        }

        private void OnTerrainDetailingFinished(object sender, AsyncJobEventArgs args)
        {
            args.AsyncJob.WorkFinished -= OnTerrainDetailingFinished;

            if (!Active)
            {
                return;
            }

            ((ChunkBuilderJob)args.AsyncJob).GetGeneratedBlockData(out _Blocks);
        }


        private void OnMeshChanged(object sender, ChunkChangedEventArgs args)
        {
            MeshChanged?.Invoke(sender, args);
        }

        private void OnMeshingFinished(object sender, AsyncJobEventArgs args)
        {
            args.AsyncJob.WorkFinished -= OnMeshingFinished;

            if (!(args.AsyncJob is ChunkMeshingJob chunkMeshingJob) || !Active)
            {
                return;
            }

            _PendingMeshData = chunkMeshingJob.GetMeshData();
        }

        #endregion
    }
}
