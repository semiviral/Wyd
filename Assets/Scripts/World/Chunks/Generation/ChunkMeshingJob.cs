#region

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using Wyd.Collections;
using Wyd.Controllers.State;
using Wyd.Controllers.System;
using Wyd.Controllers.World;
using Wyd.Jobs;
using Wyd.World.Blocks;
using Debug = System.Diagnostics.Debug;

#endregion

namespace Wyd.World.Chunks.Generation
{
    public class ChunkMeshingJob : AsyncParallelJob
    {
        private static readonly ObjectPool<MeshingBlock[]> _meshingBlockArrayPool = new ObjectPool<MeshingBlock[]>();
        private static readonly ObjectPool<MeshData> _meshDataPool = new ObjectPool<MeshData>();

        private readonly Stopwatch _Stopwatch;
        private readonly INodeCollection<ushort>[] _NeighborBlocksCollections;

        private int3 _OriginPoint;
        private INodeCollection<ushort> _BlocksCollection;
        private MeshData _MeshData;
        private MeshingBlock[] _MeshingBlocks;
        private bool _AggressiveFaceMerging;
        private TimeSpan _PreMeshingTimeSpan;
        private TimeSpan _MeshingTimeSpan;

        public ChunkMeshingJob() : base(GenerationConstants.CHUNK_SIZE, 128)
        {
            _Stopwatch = new Stopwatch();
            _NeighborBlocksCollections = new INodeCollection<ushort>[6];
        }

        protected override async Task Process()
        {
            if ((_BlocksCollection == null) || (_BlocksCollection.IsUniform && (_BlocksCollection.Value == BlockController.AirID)))
            {
                return;
            }

            _Stopwatch.Restart();

            PrepareMeshing();

            _Stopwatch.Stop();

            _PreMeshingTimeSpan = _Stopwatch.Elapsed;

            _Stopwatch.Restart();

            if (_AggressiveFaceMerging)
            {
                GenerateTraversalMesh();
            }
            else
            {
                await BatchTasksAndAwait().ConfigureAwait(false);
            }


            FinishMeshing();

            _Stopwatch.Stop();

            _MeshingTimeSpan = _Stopwatch.Elapsed;
        }

        protected override Task ProcessIndex(int index)
        {
            if (CancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            int localPosition = CompressVertex(WydMath.IndexTo3D(index, GenerationConstants.CHUNK_SIZE));
            ushort currentBlockId = _MeshingBlocks[index].ID;

            if (currentBlockId == BlockController.AirID)
            {
                return Task.CompletedTask;
            }

            bool transparentTraversal = BlockController.Current.CheckBlockHasProperty(currentBlockId, BlockDefinition.Property.Transparent);

            // iterate once over all 6 faces of given cubic space
            for (int normalIndex = 0; normalIndex < 6; normalIndex++)
            {
                // face direction always exists on a single bit, so shift 1 by the current normalIndex (0-5)
                Direction faceDirection = (Direction)(1 << normalIndex);

                // check if current index has face already
                if (_MeshingBlocks[index].HasFace(faceDirection))
                {
                    continue;
                }

                // indicates whether the current face checking direction is negative or positive
                bool isNegativeFace = (normalIndex - 3) >= 0;
                // normalIndex constrained to represent the 3 axes
                int iModulo3 = normalIndex % 3;
                int iModulo3Shift = GenerationConstants.CHUNK_SIZE_BIT_SHIFT * iModulo3;
                // axis value of the current face check direction
                // example: for iteration normalIndex == 0—which is positive X—it'd be equal to localPosition.x
                int faceCheckAxisValue = (localPosition >> iModulo3Shift) & GenerationConstants.CHUNK_SIZE_BIT_MASK;
                // indicates whether or not the face check is within the current chunk bounds
                bool isFaceCheckOutOfBounds = (!isNegativeFace && (faceCheckAxisValue == (GenerationConstants.CHUNK_SIZE - 1)))
                                              || (isNegativeFace && (faceCheckAxisValue == 0));


                if (!isFaceCheckOutOfBounds)
                {
                    // amount by integer to add to current traversal index to get 3D->1D position of facing block
                    int facedBlockIndex = index + GenerationConstants.IndexStepByNormalIndex[normalIndex];
                    // if so, index into block ids and set facingBlockId
                    ushort facedBlockId = _MeshingBlocks[facedBlockIndex].ID;

                    // if transparent, traverse so long as facing block is not the same block id
                    // if opaque, traverse so long as facing block is transparent
                    if (transparentTraversal)
                    {
                        if (currentBlockId != facedBlockId)
                        {
                            continue;
                        }
                    }
                    else if (!BlockController.Current.CheckBlockHasProperty(facedBlockId, BlockDefinition.Property.Transparent))
                    {
                        if (!isNegativeFace)
                        {
                            Direction inverseFaceDirection = (Direction)(1 << ((normalIndex + 3) % 6));
                            _MeshingBlocks[facedBlockIndex].SetFace(inverseFaceDirection);
                        }

                        continue;
                    }
                }
                else
                {
                    // this block of code translates the integer local position to the local position of the neighbor at [normalIndex]
                    int sign = isNegativeFace ? -1 : 1;
                    int iModuloComponentMask = GenerationConstants.CHUNK_SIZE_BIT_MASK << iModulo3Shift;
                    int finalLocalPosition = (~iModuloComponentMask & localPosition)
                                             | (WydMath.Wrap(((localPosition & iModuloComponentMask) >> iModulo3Shift) + sign,
                                                    GenerationConstants.CHUNK_SIZE, 0, GenerationConstants.CHUNK_SIZE - 1)
                                                << iModulo3Shift);

                    // index into neighbor blocks collections, call .GetPoint() with adjusted local position
                    // remark: if there's no neighbor at the index given, then no chunk exists there (for instance,
                    //     chunks at the edge of render distance). In this case, return NullID so no face is rendered on edges.
                    ushort facedBlockId = _NeighborBlocksCollections[normalIndex]?.GetPoint(DecompressVertex(finalLocalPosition))
                                          ?? BlockController.NullID;

                    if (transparentTraversal)
                    {
                        if (currentBlockId != facedBlockId)
                        {
                            continue;
                        }
                    }
                    else if (!BlockController.Current.CheckBlockHasProperty(facedBlockId, BlockDefinition.Property.Transparent))

                    {
                        continue;
                    }
                }

                _MeshingBlocks[index].SetFace(faceDirection);

                if (BlockController.Current.GetUVs(currentBlockId, faceDirection, out ushort textureId))
                {
                    int compressedUv = (textureId << (GenerationConstants.CHUNK_SIZE_BIT_SHIFT * 2))
                                       ^ (1 << GenerationConstants.CHUNK_SIZE_BIT_SHIFT)
                                       ^ 1;

                    int normals = GenerationConstants.NormalByIteration[normalIndex];
                    int[] compressedVertices = GenerationConstants.VerticesByIteration[normalIndex];

                    _MeshData.AddVertex((localPosition + compressedVertices[3]) | normals);
                    _MeshData.AddVertex(compressedUv & (int.MaxValue << (GenerationConstants.CHUNK_SIZE_BIT_SHIFT * 2)));

                    _MeshData.AddVertex((localPosition + compressedVertices[2]) | normals);
                    _MeshData.AddVertex(compressedUv & (int.MaxValue << GenerationConstants.CHUNK_SIZE_BIT_SHIFT));

                    _MeshData.AddVertex((localPosition + compressedVertices[1]) | normals);
                    _MeshData.AddVertex(compressedUv & ~(GenerationConstants.CHUNK_SIZE_BIT_MASK << GenerationConstants.CHUNK_SIZE_BIT_SHIFT));

                    _MeshData.AddVertex((localPosition + compressedVertices[0]) | normals);
                    _MeshData.AddVertex(compressedUv & int.MaxValue);


                    int verticesCount = _MeshData.VerticesCount / 2;
                    int transparentAsInt = Convert.ToInt32(transparentTraversal);

                    _MeshData.AddTriangle(transparentAsInt, 0 + verticesCount);
                    _MeshData.AddTriangle(transparentAsInt, 2 + verticesCount);
                    _MeshData.AddTriangle(transparentAsInt, 1 + verticesCount);
                    _MeshData.AddTriangle(transparentAsInt, 2 + verticesCount);
                    _MeshData.AddTriangle(transparentAsInt, 3 + verticesCount);
                    _MeshData.AddTriangle(transparentAsInt, 1 + verticesCount);
                }
                else { }
            }

            return Task.CompletedTask;
        }

        protected override Task ProcessFinished()
        {
            if (!CancellationToken.IsCancellationRequested)
            {
                DiagnosticsController.Current.RollingPreMeshingTimes.Enqueue(_PreMeshingTimeSpan);
                DiagnosticsController.Current.RollingMeshingTimes.Enqueue(_MeshingTimeSpan);
            }

            return Task.CompletedTask;
        }


        #region Data

        /// <summary>
        ///     Sets the data required for mesh generation.
        /// </summary>
        /// <param name="cancellationToken">Token to observe for cancellation indication.</param>
        /// <param name="originPoint">Origin point of the chunk that's being meshed.</param>
        /// <param name="blocksCollection"><see cref="INodeCollection{T}" /> of blocksCollection contained within the chunk.</param>
        /// <param name="aggressiveFaceMerging">Indicates whether to merge similarly textured faces.</param>
        public void SetData(CancellationToken cancellationToken, int3 originPoint, INodeCollection<ushort> blocksCollection,
            bool aggressiveFaceMerging)
        {
            CancellationToken = CancellationTokenSource.CreateLinkedTokenSource(AsyncJobScheduler.AbortToken, cancellationToken).Token;
            _OriginPoint = originPoint;
            _BlocksCollection = blocksCollection;
            _AggressiveFaceMerging = aggressiveFaceMerging;
        }

        /// <summary>
        ///     Clears all the <see cref="ChunkMeshingJob" />'s internal data.
        /// </summary>
        /// <remarks>
        ///     This should be called only after its mesh data has been applied to a mesh.
        ///     This is because the <see cref="MeshData" /> object is cleared and added to the
        ///     internal object pool for use in other jobs.
        /// </remarks>
        public void ClearData()
        {
            // clear existing data from mesh object
            // 'true' for trimming excess data from the lists.
            _MeshData.Clear(true);
            // add the mesh data to the internal object pool
            _meshDataPool.TryAdd(_MeshData);
            // remove the existing reference
            _MeshData = default;

            _PreMeshingTimeSpan = default;
            _MeshingTimeSpan = default;
        }

        public void ApplyMeshData(ref Mesh mesh) => _MeshData.ApplyMeshData(ref mesh);

        #endregion


        #region Generation

        private void PrepareMeshing()
        {
            Debug.Assert(_BlocksCollection != null, $"{nameof(_BlocksCollection)} should not be null when meshing is started. It's possible {nameof(SetData)}() has not been called.");
            Debug.Assert(_NeighborBlocksCollections != null, $"{nameof(_NeighborBlocksCollections)} should not be null when meshing is started. It's possible {nameof(SetData)}() has not been called.");
            Debug.Assert(_NeighborBlocksCollections.Length == 6, $"{nameof(_NeighborBlocksCollections)} should have a length of 6, one for each neighboring chunk. It's possible {nameof(SetData)}() has not been called.");

            // retrieve existing objects from object pool
            _MeshingBlocks = _meshingBlockArrayPool.Retrieve() ?? new MeshingBlock[GenerationConstants.CHUNK_SIZE_CUBED];
            _MeshData = _meshDataPool.Retrieve() ?? new MeshData(new List<int>(), new List<int>());

            int index = 0;

            for (int y = 0; y < GenerationConstants.CHUNK_SIZE; y++)
            for (int z = 0; z < GenerationConstants.CHUNK_SIZE; z++)
            for (int x = 0; x < GenerationConstants.CHUNK_SIZE; x++, index++)
            {
                _MeshingBlocks[index].ID = ((Octree)_BlocksCollection).GetPoint(x, y, z);
            }

            // unset reference to block collection to avoid use during meshing generation
            _BlocksCollection = null;

            for (int normalIndex = 0; normalIndex < 6; normalIndex++)
            {
                int3 globalPosition = _OriginPoint + (GenerationConstants.FaceNormalByIteration[normalIndex] * GenerationConstants.CHUNK_SIZE);

                if (WorldController.Current.TryGetChunk(globalPosition, out ChunkController chunkController))
                {
                    _NeighborBlocksCollections[normalIndex] = chunkController.Blocks;
                }
            }
        }

        /// <summary>
        ///     Generates the mesh data.
        /// </summary>
        /// <remarks>
        ///     The generated data is stored in the <see cref="MeshData" /> object <see cref="_MeshData" />.
        /// </remarks>
        private void GenerateTraversalMesh()
        {
            Debug.Assert(_MeshingBlocks.Length == GenerationConstants.CHUNK_SIZE_CUBED, $"{_MeshingBlocks} should be the same length as chunk data.");

            int index = 0;

            for (int y = 0; y < GenerationConstants.CHUNK_SIZE; y++)
            for (int z = 0; z < GenerationConstants.CHUNK_SIZE; z++)
            for (int x = 0; x < GenerationConstants.CHUNK_SIZE; x++, index++)
            {
                if (CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ushort currentBlockId = _MeshingBlocks[index].ID;
                int localPosition = x | (y << GenerationConstants.CHUNK_SIZE_BIT_SHIFT)
                                      | (z << (GenerationConstants.CHUNK_SIZE_BIT_SHIFT * 2));

                if (currentBlockId == BlockController.AirID)
                {
                    continue;
                }

                TraverseIndex(index, localPosition, currentBlockId,
                    BlockController.Current.CheckBlockHasProperty(currentBlockId, BlockDefinition.Property.Transparent));
            }
        }

        /// <summary>
        ///     Traverse given index of <see cref="_MeshingBlocks" /> to conditionally emit vertex data for each face.
        /// </summary>
        /// <param name="index">Current working index.</param>
        /// <param name="localPosition">3D projected local position of the current working index.</param>
        /// <param name="currentBlockId">Block ID present at the current working index.</param>
        /// <param name="transparentTraversal">Whether or not this traversal uses transparent-specific conditionals.</param>
        private void TraverseIndex(int index, int localPosition, ushort currentBlockId, bool transparentTraversal)
        {
            Debug.Assert(currentBlockId != BlockController.AirID, $"{nameof(TraverseIndex)} should not run on air blocks.");

            // iterate once over all 6 faces of given cubic space
            for (int normalIndex = 0; normalIndex < 6; normalIndex++)
            {
                // face direction always exists on a single bit, so shift 1 by the current normalIndex (0-5)
                Direction faceDirection = (Direction)(1 << normalIndex);

                // check if current index has face already
                if (_MeshingBlocks[index].HasFace(faceDirection))
                {
                    continue;
                }

                // indicates whether the current face checking direction is negative or positive
                bool isNegativeFace = (normalIndex - 3) >= 0;
                // normalIndex constrained to represent the 3 axes
                int iModulo3 = normalIndex % 3;
                int iModulo3Shift = GenerationConstants.CHUNK_SIZE_BIT_SHIFT * iModulo3;
                // axis value of the current face check direction
                // example: for iteration normalIndex == 0—which is positive X—it'd be equal to localPosition.x
                int faceCheckAxisValue = (localPosition >> iModulo3Shift) & GenerationConstants.CHUNK_SIZE_BIT_MASK;
                // indicates whether or not the face check is within the current chunk bounds
                bool isFaceCheckOutOfBounds = (!isNegativeFace && (faceCheckAxisValue == (GenerationConstants.CHUNK_SIZE - 1)))
                                              || (isNegativeFace && (faceCheckAxisValue == 0));
                // total number of successful traversals
                // remark: this is outside the for loop so that the if statement after can determine if any traversals have happened
                int traversals = 0;

                for (int perpendicularNormalIndex = 1; perpendicularNormalIndex < 3; perpendicularNormalIndex++)
                {
                    // the index of the int3 traversalNormal to traverse on
                    int traversalNormalIndex = (iModulo3 + perpendicularNormalIndex) % 3;
                    int traversalNormalShift = GenerationConstants.CHUNK_SIZE_BIT_SHIFT * traversalNormalIndex;

                    // current value of the local position by traversal direction
                    int traversalNormalAxisValue = (localPosition >> traversalNormalShift) & GenerationConstants.CHUNK_SIZE_BIT_MASK;
                    // amount by integer to add to current index to get 3D->1D position of traversal position
                    int traversalIndexStep = GenerationConstants.IndexStepByNormalIndex[traversalNormalIndex];
                    // current traversal index, which is increased by traversalIndexStep every iteration the for loop below
                    int traversalIndex = index + (traversals * traversalIndexStep);
                    // local start axis position + traversals
                    int totalTraversalLength = traversalNormalAxisValue + traversals;

                    for (;
                        (totalTraversalLength < GenerationConstants.CHUNK_SIZE)
                        && !_MeshingBlocks[traversalIndex].HasFace(faceDirection)
                        && (_MeshingBlocks[traversalIndex].ID == currentBlockId);
                        totalTraversalLength++,
                        traversals++, // increment traversals
                        traversalIndex += traversalIndexStep) // increment traversal index by index step to adjust local working position
                    {
                        // check if current facing block axis value is within the local chunk
                        if (!isFaceCheckOutOfBounds)
                        {
                            // amount by integer to add to current traversal index to get 3D->1D position of facing block
                            int facedBlockIndex = traversalIndex + GenerationConstants.IndexStepByNormalIndex[normalIndex];
                            // if so, index into block ids and set facingBlockId
                            ushort facedBlockId = _MeshingBlocks[facedBlockIndex].ID;

                            // if transparent, traverse so long as facing block is not the same block id
                            // if opaque, traverse so long as facing block is transparent
                            if (transparentTraversal)
                            {
                                if (currentBlockId != facedBlockId)
                                {
                                    break;
                                }
                            }
                            else if (!BlockController.Current.CheckBlockHasProperty(facedBlockId, BlockDefinition.Property.Transparent))
                            {
                                if (!isNegativeFace)
                                {
                                    Direction inverseFaceDirection = (Direction)(1 << ((normalIndex + 3) % 6));
                                    _MeshingBlocks[facedBlockIndex].SetFace(inverseFaceDirection);
                                }

                                break;
                            }
                        }
                        else
                        {
                            // this block of code translates the integer local position to the local position of the neighbor at [normalIndex]
                            int sign = isNegativeFace ? -1 : 1;
                            int iModuloComponentMask = GenerationConstants.CHUNK_SIZE_BIT_MASK << iModulo3Shift;
                            int translatedLocalPosition = localPosition + (traversals << traversalNormalShift);
                            int finalLocalPosition = (~iModuloComponentMask & translatedLocalPosition)
                                                     | (WydMath.Wrap(((translatedLocalPosition & iModuloComponentMask) >> iModulo3Shift) + sign,
                                                            GenerationConstants.CHUNK_SIZE, 0, GenerationConstants.CHUNK_SIZE - 1)
                                                        << iModulo3Shift);

                            // index into neighbor blocks collections, call .GetPoint() with adjusted local position
                            // remark: if there's no neighbor at the index given, then no chunk exists there (for instance,
                            //     chunks at the edge of render distance). In this case, return NullID so no face is rendered on edges.
                            ushort facedBlockId = _NeighborBlocksCollections[normalIndex]?.GetPoint(DecompressVertex(finalLocalPosition))
                                                  ?? BlockController.NullID;

                            if (transparentTraversal)
                            {
                                if (currentBlockId != facedBlockId)
                                {
                                    break;
                                }
                            }
                            else if (!BlockController.Current.CheckBlockHasProperty(facedBlockId, BlockDefinition.Property.Transparent))
                            {
                                break;
                            }
                        }

                        _MeshingBlocks[traversalIndex].SetFace(faceDirection);
                    }

                    // if it's the first traversal and we've only made a 1x1x1 face, continue to test next axis
                    if ((traversals == 1) && (perpendicularNormalIndex == 1))
                    {
                        continue;
                    }

                    if (traversals == 0 || !BlockController.Current.GetUVs(currentBlockId, faceDirection, out ushort textureId))
                    {
                        break;
                    }

                    int uvShift = (iModulo3 + traversalNormalIndex) % 2;
                    int compressedUv = (textureId << (GenerationConstants.CHUNK_SIZE_BIT_SHIFT * 2))
                                       | (1 << (GenerationConstants.CHUNK_SIZE_BIT_SHIFT * ((uvShift + 1) % 2)))
                                       | (traversals << (GenerationConstants.CHUNK_SIZE_BIT_SHIFT * uvShift));

                    int traversalShiftedMask = GenerationConstants.CHUNK_SIZE_BIT_MASK << traversalNormalShift;
                    int unaryTraversalShiftedMask = ~traversalShiftedMask;

                    int aggregatePositionNormal = localPosition | GenerationConstants.NormalByIteration[normalIndex];
                    int[] compressedVertices = GenerationConstants.VerticesByIteration[normalIndex];


                    _MeshData.AddVertex(aggregatePositionNormal
                                        + ((unaryTraversalShiftedMask & compressedVertices[3])
                                           | ((((compressedVertices[3] >> traversalNormalShift) * traversals) << traversalNormalShift)
                                              & traversalShiftedMask)));
                    _MeshData.AddVertex(compressedUv & (int.MaxValue << (GenerationConstants.CHUNK_SIZE_BIT_SHIFT * 2)));


                    _MeshData.AddVertex(aggregatePositionNormal
                                        + ((unaryTraversalShiftedMask & compressedVertices[2])
                                           | ((((compressedVertices[2] >> traversalNormalShift) * traversals) << traversalNormalShift)
                                              & traversalShiftedMask)));
                    _MeshData.AddVertex(compressedUv & (int.MaxValue << GenerationConstants.CHUNK_SIZE_BIT_SHIFT));


                    _MeshData.AddVertex(aggregatePositionNormal
                                        + ((unaryTraversalShiftedMask & compressedVertices[1])
                                           | ((((compressedVertices[1] >> traversalNormalShift) * traversals) << traversalNormalShift)
                                              & traversalShiftedMask)));
                    _MeshData.AddVertex(compressedUv & ~(GenerationConstants.CHUNK_SIZE_BIT_MASK << GenerationConstants.CHUNK_SIZE_BIT_SHIFT));


                    _MeshData.AddVertex(aggregatePositionNormal
                                        + ((unaryTraversalShiftedMask & compressedVertices[0])
                                           | ((((compressedVertices[0] >> traversalNormalShift) * traversals) << traversalNormalShift)
                                              & traversalShiftedMask)));
                    _MeshData.AddVertex(compressedUv & int.MaxValue);

                    // add triangles
                    int verticesCount = _MeshData.VerticesCount / 2;
                    int transparentAsInt = Convert.ToInt32(transparentTraversal);

                    _MeshData.AddTriangle(transparentAsInt, 0 + verticesCount);
                    _MeshData.AddTriangle(transparentAsInt, 2 + verticesCount);
                    _MeshData.AddTriangle(transparentAsInt, 1 + verticesCount);
                    _MeshData.AddTriangle(transparentAsInt, 2 + verticesCount);
                    _MeshData.AddTriangle(transparentAsInt, 3 + verticesCount);
                    _MeshData.AddTriangle(transparentAsInt, 1 + verticesCount);

                    break;
                }
            }
        }

        private void FinishMeshing()
        {
            // clear mask, add to object pool, and unset reference
            Array.Clear(_MeshingBlocks, 0, _MeshingBlocks.Length);
            _meshingBlockArrayPool.TryAdd(_MeshingBlocks);
            _MeshingBlocks = default;

            // clear array to free RAM until next execution
            Array.Clear(_NeighborBlocksCollections, 0, _NeighborBlocksCollections.Length);

            _OriginPoint = default;
            _BlocksCollection = default;
            _AggressiveFaceMerging = default;
        }

        #endregion


        #region Vertex Compression

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CompressVertex(int3 vertex) =>
            (vertex.x & GenerationConstants.CHUNK_SIZE_BIT_MASK)
            | ((vertex.y & GenerationConstants.CHUNK_SIZE_BIT_MASK) << GenerationConstants.CHUNK_SIZE_BIT_SHIFT)
            | ((vertex.z & GenerationConstants.CHUNK_SIZE_BIT_MASK) << (GenerationConstants.CHUNK_SIZE_BIT_SHIFT * 2));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int3 DecompressVertex(int vertex) =>
            new int3(vertex & GenerationConstants.CHUNK_SIZE_BIT_MASK,
                (vertex >> GenerationConstants.CHUNK_SIZE_BIT_SHIFT) & GenerationConstants.CHUNK_SIZE_BIT_MASK,
                (vertex >> (GenerationConstants.CHUNK_SIZE_BIT_SHIFT * 2)) & GenerationConstants.CHUNK_SIZE_BIT_MASK);

        #endregion


    }
}