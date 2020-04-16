#region

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Wyd.Controllers.State;
using Wyd.Controllers.System;
using Wyd.Game;
using Wyd.Game.World.Blocks;
using Wyd.Game.World.Chunks;
using Wyd.Game.World.Chunks.Events;
using Wyd.System;
using Wyd.System.Collections;
using Wyd.System.Jobs;

#endregion

namespace Wyd.Controllers.World.Chunk
{
    [Flags]
    public enum State
    {
        Terrain = 0b0000_0001,
        AwaitingTerrain = 0b0000_0011,
        TerrainComplete = 0b0000_1111,
        UpdateMesh = 0b0001_0000,
        Meshing = 0b0010_0000,
        MeshDataPending = 0b0100_0000,
        Meshed = 0b1000_0000
    }

    public class ChunkController : ActivationStateChunkController, IPerFrameIncrementalUpdate
    {
        private static readonly ObjectCache<BlockAction> _blockActionsCache = new ObjectCache<BlockAction>(true, 1024);

        public static readonly int3 Size = new int3(32);

        #region INSTANCE MEMBERS

        private CancellationTokenSource _CancellationTokenSource;
        private Queue<BlockAction> _BlockActions;
        private OctreeNode _Blocks;
        private Mesh _Mesh;
        private bool _Visible;
        private bool _RenderShadows;

        private long _State;
        private ChunkMeshData _PendingMeshData;

        public ref OctreeNode Blocks => ref _Blocks;

        public State State
        {
            get => (State)Interlocked.Read(ref _State);
            private set
            {
                Interlocked.Exchange(ref _State, (long)value);

#if UNITY_EDITOR

                BinaryState = Convert.ToString(unchecked((byte)State), 2);

#endif
            }
        }

        public bool RenderShadows
        {
            get => _RenderShadows;
            set
            {
                if (_RenderShadows == value)
                {
                    return;
                }

                _RenderShadows = value;
                MeshRenderer.receiveShadows = _RenderShadows;
                MeshRenderer.shadowCastingMode = _RenderShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
        }

        public bool Visible
        {
            get => _Visible;
            set
            {
                if (_Visible == value)
                {
                    return;
                }

                _Visible = value;
                MeshRenderer.enabled = _Visible;
            }
        }

        #endregion

        #region SERIALIZED MEMBERS

        [SerializeField]
        private MeshRenderer MeshRenderer;

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
#endif

        #endregion


        protected override void Awake()
        {
            base.Awake();

            _BlockActions = new Queue<BlockAction>();
            _Mesh = new Mesh();

            void FlagUpdateMesh(object sender, ChunkChangedEventArgs args)
            {
                FlagMeshForUpdate();
            }

            BlocksChanged += FlagUpdateMesh;
            TerrainChanged += FlagUpdateMesh;

            MeshRenderer.materials = TextureController.Current.TerrainMaterials;
            _Visible = MeshRenderer.enabled;
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            PerFrameUpdateController.Current.RegisterPerFrameUpdater(20, this);
            _CancellationTokenSource = new CancellationTokenSource();
            _Visible = MeshRenderer.enabled;
            _BlockActions.Clear();

            Blocks = new OctreeNode(OriginPoint + (Size / new float3(2f)), Size.x, 0);
            State = State.Terrain;

#if UNITY_EDITOR

            MinimumPoint = OriginPoint;
            MaximumPoint = OriginPoint + Size;
            Extents = WydMath.ToFloat(Size) / 2f;

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

            _BlockActions.Clear();
        }

        public void FrameUpdate()
        {
            if (State.HasFlag(State.TerrainComplete)
                && State.HasFlag(State.Meshed)
                && !State.HasFlag(State.UpdateMesh))
            {
                return;
            }


            if (State.HasFlag(State.Terrain)
                && !State.HasFlag(State.AwaitingTerrain)
                && WorldController.Current.ReadyForGeneration)
            {
                State |= (State & ~State.TerrainComplete) | State.AwaitingTerrain;
                TerrainController.BeginTerrainGeneration(ref Blocks, _CancellationTokenSource.Token,
                    OnTerrainGenerationFinished);
            }

            if (!State.HasFlag(State.TerrainComplete))
            {
                return;
            }

            if (State.HasFlag(State.MeshDataPending) && (_PendingMeshData != null))
            {
                MeshController.ApplyMesh(_PendingMeshData);
                _PendingMeshData = null;
                State = (State & ~State.MeshDataPending) | State.Meshed;
                OnMeshChanged(this, new ChunkChangedEventArgs(OriginPoint, Enumerable.Empty<int3>()));
            }

            if (!State.HasFlag(State.Meshing)
                && !State.HasFlag(State.MeshDataPending)
                && (!State.HasFlag(State.Meshed) || State.HasFlag(State.UpdateMesh))
                && WorldController.Current.NeighborsTerrainComplete(OriginPoint))
            {
                State = (State & ~(State.Meshed | State.UpdateMesh)) | State.Meshing;
                MeshController.BeginGeneratingMesh(Blocks, _CancellationTokenSource.Token, OnMeshingFinished);
            }
        }

        public IEnumerable IncrementalFrameUpdate()
        {
            if (!State.HasFlag(State.TerrainComplete))
            {
                yield break;
            }

            while (_BlockActions.Count > 0)
            {
                BlockAction blockAction = _BlockActions.Dequeue();

                ProcessBlockAction(blockAction);

                _blockActionsCache.CacheItem(ref blockAction);

                yield return null;
            }
        }

        #region DE/ACTIVATION

        public void Activate(float3 position)
        {
            _SelfTransform.position = position;

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
                TryGetNeighborsRequiringUpdateNormals(blockAction.GlobalPosition, out IEnumerable<int3> normals)
                    ? new ChunkChangedEventArgs(OriginPoint, normals)
                    : new ChunkChangedEventArgs(OriginPoint, Enumerable.Empty<int3>()));
        }

        private bool TryGetNeighborsRequiringUpdateNormals(float3 globalPosition, out IEnumerable<int3> normals)
        {
            normals = Enumerable.Empty<int3>();

            float3 localPosition = globalPosition - (OriginPoint + (WydMath.ToFloat(Size) / 2f));
            float3 localPositionSign = math.sign(localPosition);
            float3 localPositionAbs = math.abs(math.ceil(localPosition + (new float3(0.5f) * localPositionSign)));

            if (!math.any(localPositionAbs == 16f))
            {
                return false;
            }

            normals = WydMath.ToComponents(WydMath.ToInt(math.floor(localPositionAbs / 16f) * localPositionSign));
            return true;
        }

        private void ModifyBlockPosition(float3 globalPosition, ushort newId)
        {
            if (!Blocks.ContainsMinBiased(globalPosition))
            {
                return;
            }

            Blocks.SetPoint(globalPosition, newId);
        }

        public bool TryGetBlockAt(float3 globalPosition, out ushort blockId)
        {
            blockId = 0;

            if (!Blocks.ContainsMinBiased(globalPosition))
            {
                return false;
            }

            blockId = Blocks.GetPoint(globalPosition);

            return true;
        }

        public bool TryPlaceBlockAt(float3 globalPosition, ushort newBlockId) =>
            Blocks.ContainsMinBiased(globalPosition)
            && TryGetBlockAt(globalPosition, out ushort blockId)
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


        public void FlagMeshForUpdate()
        {
            if (!State.HasFlag(State.UpdateMesh))
            {
                State |= State.UpdateMesh;
            }
        }


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
            State |= State.TerrainComplete;
            args.AsyncJob.WorkFinished -= OnTerrainGenerationFinished;
            OnLocalTerrainChanged(sender, new ChunkChangedEventArgs(OriginPoint, Directions.AllDirectionAxes));
        }

        private void OnMeshChanged(object sender, ChunkChangedEventArgs args)
        {
            MeshChanged?.Invoke(sender, args);
        }

        private void OnMeshingFinished(object sender, AsyncJobEventArgs args)
        {
            if (!(args.AsyncJob is ChunkMeshingJob chunkMeshingJob))
            {
                return;
            }

            _PendingMeshData = chunkMeshingJob.GetMeshData();
            State = (State & ~State.Meshing) | State.MeshDataPending;
            args.AsyncJob.WorkFinished -= OnMeshingFinished;
        }

        #endregion
    }
}
