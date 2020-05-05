#region

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;
using Wyd.Controllers.State;
using Wyd.Controllers.World;
using Wyd.Game.World.Blocks;
using Wyd.System;
using Wyd.System.Collections;
using Wyd.System.Graphics;

#endregion

namespace Wyd.Game.World.Chunks.Generation
{
    public class ChunkMesher
    {
        private readonly Stopwatch _Stopwatch;
        private readonly BlockFaces[] _Mask;
        private readonly MeshData _MeshData;

        private CancellationToken _CancellationToken;
        private float3 _OriginPoint;
        private bool _AggressiveFaceMerging;
        private INodeCollection<ushort> _Blocks;
        private List<INodeCollection<ushort>> _NeighborNodes;

        public TimeSpan SetBlockTimeSpan { get; private set; }
        public TimeSpan MeshingTimeSpan { get; private set; }

        public ChunkMesher(CancellationToken cancellationToken, float3 originPoint, INodeCollection<ushort> blocks, bool aggressiveFaceMerging)
        {
            if (blocks == null)
            {
                return;
            }

            _Stopwatch = new Stopwatch();
            _MeshData = new MeshData(new List<Vector3>(), new List<Vector3>(),
                new List<int>(), // triangles
                new List<int>()); // transparent triangles
            _Mask = new BlockFaces[ChunkController.SIZE_CUBED];

            PrepareMeshing(cancellationToken, originPoint, blocks, aggressiveFaceMerging);
        }


        #region Runtime

        public void PrepareMeshing(CancellationToken cancellationToken, float3 originPoint, INodeCollection<ushort> blocks,
            bool aggressiveFaceMerging)
        {
            _CancellationToken = cancellationToken;
            _OriginPoint = originPoint;
            _Blocks = blocks;
            _AggressiveFaceMerging = aggressiveFaceMerging;

            _MeshData.Clear(true);
        }

        public void Reset()
        {
            _MeshData.Clear(true);
            _Blocks = null;
        }

        public void ApplyMeshData(ref Mesh mesh) => _MeshData.ApplyMeshData(ref mesh);

        public void GenerateMesh()
        {
            if ((_Blocks == null) || (_Blocks.IsUniform && (_Blocks.Value == BlockController.AirID)))
            {
                return;
            }

            _Stopwatch.Restart();

            if (_NeighborNodes == null)
            {
                _NeighborNodes = new List<INodeCollection<ushort>>(6);
            }

            _NeighborNodes.Clear();

            for (int i = 0; i < 6; i++)
            {
                _NeighborNodes.Add(null);
            }

            // todo provide this data from ChunkMeshController maybe?
            foreach ((int3 normal, ChunkController chunkController) in WorldController.Current.GetNeighboringChunksWithNormal(_OriginPoint))
            {
                int index = -1;

                if (normal.x != 0)
                {
                    index = normal.x > 0 ? 0 : 3;
                }
                else if (normal.y != 0)
                {
                    index = normal.y > 0 ? 1 : 4;
                }
                else if (normal.z != 0)
                {
                    index = normal.z > 0 ? 2 : 5;
                }

                _NeighborNodes[index] = chunkController.Blocks;
            }

            // clear masks for new meshing
            Array.Clear(_Mask, 0, _Mask.Length);

            _Stopwatch.Stop();

            SetBlockTimeSpan = _Stopwatch.Elapsed;

            _Stopwatch.Restart();

            for (int index = 0; index < _Mask.Length; index++)
            {
                if (_CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                int3 localPosition = WydMath.IndexTo3D(index, ChunkController.SIZE);
                int3 globalPosition = WydMath.ToInt(_OriginPoint + localPosition);

                ushort currentBlockId = _Blocks.GetPoint(localPosition);

                if (currentBlockId == BlockController.AirID)
                {
                    continue;
                }

                TraverseIndex(index, globalPosition, localPosition, currentBlockId,
                    BlockController.Current.CheckBlockHasProperty(currentBlockId, BlockDefinition.Property.Transparent));
            }

            _Stopwatch.Stop();
            MeshingTimeSpan = _Stopwatch.Elapsed;
        }

        #endregion


        #region Traversal Meshing

        private void TraverseIndex(int index, int3 globalPosition, int3 localPosition, ushort currentBlockId, bool transparentTraversal)
        {
            for (int normalIndex = 0; normalIndex < 6; normalIndex++)
            {
                Direction faceDirection = (Direction)(1 << normalIndex);

                if (_Mask[index].HasFace(faceDirection))
                {
                    continue;
                }

                int iModulo3 = normalIndex % 3;
                int traversalIterations = 0;
                int traversals = 0;

                for (int perpendicularNormalIndex = 0; perpendicularNormalIndex < 2; perpendicularNormalIndex++)
                {
                    int traversalNormalAxisIndex = (iModulo3 + 1) % 3;
                    int3 traversalNormal = new int3(0)
                    {
                        [traversalNormalAxisIndex] = 1
                    };

                    int sliceIndexValue = localPosition[traversalNormalAxisIndex];
                    int maximumTraversals = _AggressiveFaceMerging ? ChunkController.SIZE : sliceIndexValue + 1;
                    int traversalFactor = GenerationConstants.IndexStepByTraversalNormalIndex[traversalNormalAxisIndex];

                    for (; (sliceIndexValue + traversals) < maximumTraversals; traversals++)
                    {
                        int traversalIndex = index + (traversals * traversalFactor);
                        int3 currentTraversalPosition = localPosition + (traversalNormal * traversals);

                        if (_Mask[traversalIndex].HasFace(faceDirection)
                            || ((traversals > 0) && (_Blocks.GetPoint(currentTraversalPosition) != currentBlockId)))
                        {
                            break;
                        }

                        int3 faceNormal = GenerationConstants.FaceNormalByIteration[normalIndex];
                        int3 traversalFacingBlockPosition = currentTraversalPosition + faceNormal;
                        int facingPositionAxisValue = traversalFacingBlockPosition[iModulo3];

                        ushort facingBlockId;
                        if ((facingPositionAxisValue > 0) && (facingPositionAxisValue < ChunkController.SIZE_MINUS_ONE))
                        {
                            facingBlockId = _Blocks.GetPoint(traversalFacingBlockPosition);
                        }
                        else
                        {
                            int3 relativeLocalPosition = math.abs(traversalFacingBlockPosition + (faceNormal * -ChunkController.SIZE_MINUS_ONE));

                            facingBlockId = _NeighborNodes[normalIndex]?.GetPoint(relativeLocalPosition) ?? BlockController.NullID;
                        }

                        // if transparent, traverse as long as block is the same
                        // if opaque, traverse as long as faceNormal-adjacent block is transparent
                        if ((transparentTraversal && (currentBlockId != facingBlockId))
                            || !BlockController.Current.CheckBlockHasProperty(facingBlockId, BlockDefinition.Property.Transparent))
                        {
                            break;
                        }

                        _Mask[traversalIndex].SetFace(faceDirection);
                    }

                    // if we haven't traversed at all, continue to next axis
                    if ((traversals == 0) || ((traversalIterations == 0) && (traversals == 1)))
                    {
                        traversalIterations += 1;
                        continue;
                    }

                    // add triangles
                    int verticesCount = _MeshData.VerticesCount;
                    int transparentAsInt = Convert.ToInt32(transparentTraversal);

                    // ReSharper disable once ForCanBeConvertedToForeach
                    for (int triangleIndex = 0; triangleIndex < BlockFaces.Triangles.FaceTriangles.Length; triangleIndex++)
                    {
                        _MeshData.AddTriangle(transparentAsInt, BlockFaces.Triangles.FaceTriangles[triangleIndex] + verticesCount);
                    }

                    // add vertices
                    int3 traversalVertexOffset = math.max(traversals * traversalNormal, 1);
                    float3[] vertices = BlockFaces.Vertices.FaceVerticesByNormalIndex[normalIndex];

                    // ReSharper disable once ForCanBeConvertedToForeach
                    for (int verticesIndex = 0; verticesIndex < vertices.Length; verticesIndex++)
                    {
                        _MeshData.AddVertex(localPosition + (vertices[verticesIndex] * traversalVertexOffset));
                    }

                    if (BlockController.Current.GetUVs(currentBlockId, globalPosition, faceDirection, new float2(1f)
                    {
                        [GenerationConstants.UVIndexAdjustments[iModulo3][traversalNormalAxisIndex]] = traversals
                    }, out BlockUVs blockUVs))
                    {
                        _MeshData.AddUV(blockUVs.TopLeft);
                        _MeshData.AddUV(blockUVs.TopRight);
                        _MeshData.AddUV(blockUVs.BottomLeft);
                        _MeshData.AddUV(blockUVs.BottomRight);
                    }

                    break;
                }
            }
        }

        #endregion
    }
}