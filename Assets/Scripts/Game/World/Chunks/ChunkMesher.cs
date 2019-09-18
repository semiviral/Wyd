#region

using System.Collections.Generic;
using System.Threading;
using Controllers.State;
using Controllers.World;
using Game.World.Blocks;
using UnityEngine;
using UnityEngine.Rendering;

// ReSharper disable TooWideLocalVariableScope

#endregion

namespace Game.World.Chunks
{
    public class ChunkMesher
    {
        private readonly List<int> _Triangles;
        private readonly List<Vector3> _Vertices;
        private readonly List<Vector3> _UVs;

        public CancellationToken AbortToken;
        public Bounds Bounds;
        public Block[] Blocks;
        public bool AggressiveFaceMerging;

        public ChunkMesher()
        {
            _Triangles = new List<int>();
            _Vertices = new List<Vector3>();
            _UVs = new List<Vector3>();
        }

        public ChunkMesher(
            Bounds bounds, Block[] blocks, bool aggressiveFaceMerging,
            CancellationToken abortToken) : this()
        {
            AbortToken = abortToken;
            Bounds = bounds;
            Blocks = blocks;
            AggressiveFaceMerging = aggressiveFaceMerging;
        }

        public void ClearInternalData()
        {
            _Vertices.Clear();
            _Triangles.Clear();
            _UVs.Clear();
        }

        public void GenerateMesh()
        {
            for (int index = 0; (index < Blocks.Length) && !AbortToken.IsCancellationRequested; index++)
            {
                if (Blocks[index].Id == BlockController.BLOCK_EMPTY_ID)
                {
                    continue;
                }

                if (Blocks[index].Transparent)
                {
                    TraverseIndexTransparent(index);
                }
                else
                {
                    TraverseIndex(index);
                }
            }
        }

        /// <summary>
        ///     Applies and returns processed <see cref="UnityEngine.Mesh" />.
        /// </summary>
        /// <param name="mesh">Given <see cref="UnityEngine.Mesh" /> to apply processed data to.</param>
        /// <returns>Processed <see cref="UnityEngine.Mesh" />.</returns>
        public void SetMesh(ref Mesh mesh)
        {
            if ((_Vertices.Count == 0) || (_Triangles.Count == 0))
            {
                return;
            }

            if (mesh == default)
            {
                mesh = new Mesh();
            }
            else
            {
                mesh.Clear();
            }

            mesh.indexFormat = _Vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;

            mesh.SetVertices(_Vertices);
            mesh.SetTriangles(_Triangles, 0);

            // in case of no UVs to apply to mesh
            if (_UVs.Count > 0)
            {
                mesh.SetUVs(0, _UVs);
            }

            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
        }

        #region SIMPLER MESHING

        private void TraverseIndexTransparent(int index)
        {
            (int x, int y, int z) = Mathv.GetVector3IntIndex(index, ChunkController.Size);
            Vector3 localPosition = new Vector3(x, y, z);
            Vector3 globalPosition = Bounds.min + localPosition;

            // todo implement the rest of the directions
            
            if ((((y == (ChunkController.Size.y - 1))
                  && WorldController.Current.TryGetBlockAt(globalPosition + Vector3.up, out Block block)
                  && (block.Id != Blocks[index].Id))
                 || ((y < (ChunkController.Size.y - 1))
                     && (Blocks[index + ChunkController.YIndexStep].Id != Blocks[index].Id)))
                && !Blocks[index].HasFace(Direction.Up))
            {
                Blocks[index].SetFace(Direction.Up, true);
                AddTriangles(Direction.Up);

                int traversals;
                Vector3 uvSize = Vector3.one;

                if (AggressiveFaceMerging)
                {
                    traversals = GetTraversals(index, globalPosition, z, Direction.Up, Direction.North,
                        ChunkController.Size.x, ChunkController.Size.z);

                    if (traversals > 1)
                    {
                        _Vertices.Add(localPosition + new Vector3(0f, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(1f, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, 1f, traversals));
                        _Vertices.Add(localPosition + new Vector3(1f, 1f, traversals));
                        uvSize.x = traversals;
                    }
                    else
                    {
                        traversals = GetTraversals(index, globalPosition, x, Direction.Up, Direction.East, 1,
                            ChunkController.Size.x);

                        _Vertices.Add(localPosition + new Vector3(0f, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(traversals, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, 1f, 1f));
                        _Vertices.Add(localPosition + new Vector3(traversals, 1f, 1f));
                        uvSize.z = traversals;
                    }
                }
                else
                {
                    AddVertices(Direction.Up, localPosition);
                }

                if (BlockController.Current.GetBlockSpriteUVs(Blocks[index].Id, globalPosition,
                    Direction.Up, uvSize, out Vector3[] uvs))
                {
                    _UVs.Add(uvs[0]);
                    _UVs.Add(uvs[2]);
                    _UVs.Add(uvs[1]);
                    _UVs.Add(uvs[3]);
                }
            }
        }

        private void TraverseIndex(int index)
        {
            (int x, int y, int z) = Mathv.GetVector3IntIndex(index, ChunkController.Size);
            Vector3 localPosition = new Vector3(x, y, z);
            Vector3 globalPosition = Bounds.min + localPosition;

            // ensure this block face hasn't already been traversed
            if (!Blocks[index].HasFace(Direction.North)
                // check if we're on the far edge of the chunk, and if so, query WorldController for blocks in adjacent chunk
                && (((z == (ChunkController.Size.z - 1))
                     && WorldController.Current.TryGetBlockAt(globalPosition + Vector3.forward, out Block block)
                     && block.Transparent)
                    // however if we're inside the chunk, use the proper Blocks[] array index for check 
                    || ((z < (ChunkController.Size.z - 1)) && Blocks[index + ChunkController.Size.x].Transparent)))
            {
                // set face of current block so it isn't traversed over
                Blocks[index].SetFace(Direction.North, true);
                // add triangles for this block face
                AddTriangles(Direction.North);

                int traversals;
                Vector3 uvSize = Vector3.one;

                if (AggressiveFaceMerging)
                {
                    traversals = GetTraversals(index, globalPosition, x, Direction.North, Direction.East, 1,
                        ChunkController.Size.x);

                    if (traversals > 1)
                    {
                        // The traversals value goes into the vertex points that have a positive value
                        // on the same axis as your slice value.
                        // So for instance, we were traversing on the x, so we'll be extending the x point of our
                        // vertices by the number of successful traversals.
                        _Vertices.Add(localPosition + new Vector3(0f, 0f, 1f));
                        _Vertices.Add(localPosition + new Vector3(0f, 1f, 1f));
                        _Vertices.Add(localPosition + new Vector3(traversals, 0f, 1f));
                        _Vertices.Add(localPosition + new Vector3(traversals, 1f, 1f));
                        uvSize.x = traversals;
                    }
                    else
                    {
                        // if traversal failed (no blocks found in probed direction) then look on next axis
                        traversals = GetTraversals(index, globalPosition, y, Direction.North, Direction.Up,
                            ChunkController.YIndexStep, ChunkController.Size.y);

                        _Vertices.Add(localPosition + new Vector3(0f, 0f, 1f));
                        _Vertices.Add(localPosition + new Vector3(0f, traversals, 1f));
                        _Vertices.Add(localPosition + new Vector3(1f, 0f, 1f));
                        _Vertices.Add(localPosition + new Vector3(1f, traversals, 1f));
                        uvSize.z = traversals;
                    }
                }
                else
                {
                    // no traversals found, so just add vertices for a regular 1x1 face
                    AddVertices(Direction.North, localPosition);
                }

                // attempt to retrieve and add uvs for block face
                if (BlockController.Current.GetBlockSpriteUVs(Blocks[index].Id, globalPosition,
                    Direction.North, uvSize, out Vector3[] uvs))
                {
                    _UVs.Add(uvs[1]);
                    _UVs.Add(uvs[3]);
                    _UVs.Add(uvs[0]);
                    _UVs.Add(uvs[2]);
                }
            }

            if (!Blocks[index].HasFace(Direction.East)
                && (((x == (ChunkController.Size.x - 1))
                     && WorldController.Current.TryGetBlockAt(globalPosition + Vector3.right, out block)
                     && block.Transparent)
                    || ((x < (ChunkController.Size.x - 1)) && Blocks[index + 1].Transparent)))
            {
                Blocks[index].SetFace(Direction.East, true);
                AddTriangles(Direction.East);

                int traversals;
                Vector3 uvSize = Vector3.one;

                if (AggressiveFaceMerging)
                {
                    traversals = GetTraversals(index, globalPosition, z, Direction.East, Direction.North,
                        ChunkController.Size.x, ChunkController.Size.z);

                    if (traversals > 1)
                    {
                        _Vertices.Add(localPosition + new Vector3(1f, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(1f, 0f, traversals));
                        _Vertices.Add(localPosition + new Vector3(1f, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(1f, 1f, traversals));
                        uvSize.x = traversals;
                    }
                    else
                    {
                        traversals = GetTraversals(index, globalPosition, y, Direction.East, Direction.Up,
                            ChunkController.YIndexStep, ChunkController.Size.y);

                        _Vertices.Add(localPosition + new Vector3(1f, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(1f, 0f, 1f));
                        _Vertices.Add(localPosition + new Vector3(1f, traversals, 0f));
                        _Vertices.Add(localPosition + new Vector3(1f, traversals, 1f));
                        uvSize.z = traversals;
                    }
                }
                else
                {
                    AddVertices(Direction.East, localPosition);
                }

                if (BlockController.Current.GetBlockSpriteUVs(Blocks[index].Id, globalPosition,
                    Direction.East, uvSize, out Vector3[] uvs))
                {
                    _UVs.Add(uvs[0]);
                    _UVs.Add(uvs[1]);
                    _UVs.Add(uvs[2]);
                    _UVs.Add(uvs[3]);
                }
            }

            if (!Blocks[index].HasFace(Direction.South)
                && (((z == 0)
                     && WorldController.Current.TryGetBlockAt(globalPosition + Vector3.back, out block)
                     && block.Transparent)
                    || ((z > 0) && Blocks[index - ChunkController.Size.x].Transparent)))
            {
                Blocks[index].SetFace(Direction.South, true);
                AddTriangles(Direction.South);

                int traversals;
                Vector3 uvSize = Vector3.one;

                if (AggressiveFaceMerging)
                {
                    traversals = GetTraversals(index, globalPosition, x, Direction.South, Direction.East, 1,
                        ChunkController.Size.x);

                    if (traversals > 1)
                    {
                        _Vertices.Add(localPosition + new Vector3(0f, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(traversals, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(traversals, 1f, 0f));
                        uvSize.x = traversals;
                    }
                    else
                    {
                        traversals = GetTraversals(index, globalPosition, y, Direction.South, Direction.Up,
                            ChunkController.YIndexStep, ChunkController.Size.y);

                        _Vertices.Add(localPosition + new Vector3(0f, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(1f, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, traversals, 0f));
                        _Vertices.Add(localPosition + new Vector3(1f, traversals, 0f));
                        uvSize.z = traversals;
                    }
                }
                else
                {
                    AddVertices(Direction.South, localPosition);
                }

                if (BlockController.Current.GetBlockSpriteUVs(Blocks[index].Id, globalPosition,
                    Direction.South, uvSize, out Vector3[] uvs))
                {
                    _UVs.Add(uvs[0]);
                    _UVs.Add(uvs[1]);
                    _UVs.Add(uvs[2]);
                    _UVs.Add(uvs[3]);
                }
            }

            if (!Blocks[index].HasFace(Direction.West)
                && (((x == 0)
                     && WorldController.Current.TryGetBlockAt(globalPosition + Vector3.left, out block)
                     && block.Transparent)
                    || ((x > 0) && Blocks[index - 1].Transparent)))
            {
                Blocks[index].SetFace(Direction.West, true);
                AddTriangles(Direction.West);

                int traversals;
                Vector3 uvSize = Vector3.one;

                if (AggressiveFaceMerging)
                {
                    traversals = GetTraversals(index, globalPosition, z, Direction.West, Direction.North,
                        ChunkController.Size.x, ChunkController.Size.z);

                    if (traversals > 1)
                    {
                        _Vertices.Add(localPosition + new Vector3(0f, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, 0f, traversals));
                        _Vertices.Add(localPosition + new Vector3(0f, 1f, traversals));
                        uvSize.x = traversals;
                    }
                    else
                    {
                        traversals = GetTraversals(index, globalPosition, y, Direction.West, Direction.Up,
                            ChunkController.YIndexStep, ChunkController.Size.y);

                        _Vertices.Add(localPosition + new Vector3(0f, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, traversals, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, 0f, 1f));
                        _Vertices.Add(localPosition + new Vector3(0f, traversals, 1f));
                        uvSize.z = traversals;
                    }
                }
                else
                {
                    AddVertices(Direction.West, localPosition);
                }

                if (BlockController.Current.GetBlockSpriteUVs(Blocks[index].Id, globalPosition,
                    Direction.West, uvSize, out Vector3[] uvs))
                {
                    _UVs.Add(uvs[1]);
                    _UVs.Add(uvs[3]);
                    _UVs.Add(uvs[0]);
                    _UVs.Add(uvs[2]);
                }
            }

            if (!Blocks[index].HasFace(Direction.Up)
                && (((y == (ChunkController.Size.y - 1))
                     && WorldController.Current.TryGetBlockAt(globalPosition + Vector3.up, out block)
                     && block.Transparent)
                    || ((y < (ChunkController.Size.y - 1)) && Blocks[index + ChunkController.YIndexStep].Transparent)))
            {
                Blocks[index].SetFace(Direction.Up, true);
                AddTriangles(Direction.Up);

                int traversals;
                Vector3 uvSize = Vector3.one;

                if (AggressiveFaceMerging)
                {
                    traversals = GetTraversals(index, globalPosition, z, Direction.Up, Direction.North,
                        ChunkController.Size.x, ChunkController.Size.z);

                    if (traversals > 1)
                    {
                        _Vertices.Add(localPosition + new Vector3(0f, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(1f, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, 1f, traversals));
                        _Vertices.Add(localPosition + new Vector3(1f, 1f, traversals));
                        uvSize.x = traversals;
                    }
                    else
                    {
                        traversals = GetTraversals(index, globalPosition, x, Direction.Up, Direction.East, 1,
                            ChunkController.Size.x);

                        _Vertices.Add(localPosition + new Vector3(0f, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(traversals, 1f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, 1f, 1f));
                        _Vertices.Add(localPosition + new Vector3(traversals, 1f, 1f));
                        uvSize.z = traversals;
                    }
                }
                else
                {
                    AddVertices(Direction.Up, localPosition);
                }

                if (BlockController.Current.GetBlockSpriteUVs(Blocks[index].Id, globalPosition,
                    Direction.Up, uvSize, out Vector3[] uvs))
                {
                    _UVs.Add(uvs[0]);
                    _UVs.Add(uvs[2]);
                    _UVs.Add(uvs[1]);
                    _UVs.Add(uvs[3]);
                }
            }

            // ignore the very bottom face of the world to reduce verts/tris
            if (!Blocks[index].HasFace(Direction.Down)
                && (y > 0)
                && Blocks[index - ChunkController.YIndexStep].Transparent)
            {
                Blocks[index].SetFace(Direction.Down, true);
                AddTriangles(Direction.Down);

                int traversals;
                Vector3 uvSize = Vector3.one;

                if (AggressiveFaceMerging)
                {
                    traversals = GetTraversals(index, globalPosition, z, Direction.Down, Direction.North,
                        ChunkController.Size.x, ChunkController.Size.z);

                    if (traversals > 1)
                    {
                        _Vertices.Add(localPosition + new Vector3(0f, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, 0f, traversals));
                        _Vertices.Add(localPosition + new Vector3(1f, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(1f, 0f, traversals));
                        uvSize.x = traversals;
                    }
                    else
                    {
                        traversals = GetTraversals(index, globalPosition, x, Direction.Down, Direction.East, 1,
                            ChunkController.Size.x);

                        _Vertices.Add(localPosition + new Vector3(0f, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(0f, 0f, 1f));
                        _Vertices.Add(localPosition + new Vector3(traversals, 0f, 0f));
                        _Vertices.Add(localPosition + new Vector3(traversals, 0f, 1f));
                        uvSize.z = traversals;
                    }
                }
                else
                {
                    AddVertices(Direction.Down, localPosition);
                }

                if (BlockController.Current.GetBlockSpriteUVs(Blocks[index].Id, globalPosition,
                    Direction.Down, uvSize, out Vector3[] uvs))
                {
                    _UVs.Add(uvs[0]);
                    _UVs.Add(uvs[1]);
                    _UVs.Add(uvs[2]);
                    _UVs.Add(uvs[3]);
                }
            }
        }

        private void AddTriangles(Direction direction)
        {
            int[] triangles = BlockFaces.Triangles.FaceTriangles[direction];

            foreach (int triangleValue in triangles)
            {
                _Triangles.Add(_Vertices.Count + triangleValue);
            }
        }

        private void AddVertices(Direction direction, Vector3 localPosition)
        {
            Vector3[] vertices = BlockFaces.Vertices.FaceVertices[direction];

            foreach (Vector3 vertex in vertices)
            {
                _Vertices.Add(vertex + localPosition);
            }
        }

        /// <summary>
        ///     Gets the total amount of possible traversals for face merging in a direction
        /// </summary>
        /// <param name="index">1D index of current block.</param>
        /// <param name="globalPosition">Global position of starting block.</param>
        /// <param name="slice">Current slice (x, y, or z) of a 3D index relative to your traversal direction.</param>
        /// <param name="traversalDirection">Direction to traverse in.</param>
        /// <param name="faceDirection">Direction to check faces while traversing.</param>
        /// <param name="traversalFactor">Amount of indexes to move forwards for each successful traversal in given direction.</param>
        /// <param name="limitingSliceValue">Maximum amount of traversals in given traversal direction.</param>
        /// <returns><see cref="int" /> representing how many successful traversals were made in the given direction.</returns>
        private int GetTraversals(
            int index, Vector3 globalPosition, int slice, Direction faceDirection,
            Direction traversalDirection, int traversalFactor, int limitingSliceValue)
        {
            // 1 being the current block at `index`
            int traversals = 1;

            // todo make aggressive face merging compatible with special block shapes
            if (!AggressiveFaceMerging)
            {
                return traversals;
            }

            // incrementing on x, so the traversal factor is 1
            // if we were incrementing on z, the factor would be Chunk.Size.x
            // and on y it would be (Chunk.YIndexStep)
            int traversalIndex = index + (traversals * traversalFactor);

            while ( // Set traversalIndex and ensure it is within the chunk's context
                ((slice + traversals) < limitingSliceValue)
                // This check removes the need to check if the adjacent block is transparent,
                // as our current block will never be transparent
                && (Blocks[index].Id == Blocks[traversalIndex].Id)
                && !Blocks[traversalIndex].HasFace(faceDirection)
                // ensure the block to the north of our current block is transparent
                && WorldController.Current.TryGetBlockAt(
                    globalPosition + (traversals * traversalDirection.AsVector3()) + faceDirection.AsVector3(),
                    out Block block)
                && block.Transparent)
            {
                Blocks[traversalIndex].SetFace(faceDirection, true);

                // increment and set traversal values
                traversals++;
                traversalIndex = index + (traversals * traversalFactor);
            }

            return traversals;
        }

        #endregion
    }
}
