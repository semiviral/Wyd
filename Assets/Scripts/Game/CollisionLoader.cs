#region

using System;
using System.Collections.Generic;
using System.Linq;
using Controllers.State;
using Controllers.World;
using Game.World.Blocks;
using UnityEngine;

#endregion

namespace Game
{
    public class CollisionLoader : MonoBehaviour
    {
        private static readonly ObjectCache<GameObject> ColliderCubeCache =
            new ObjectCache<GameObject>(DeactivateGameObject);

        private static GameObject DeactivateGameObject(GameObject obj)
        {
            obj.SetActive(false);
            return obj;
        }

        private Transform _SelfTransform;
        private Vector3 _LastCalculatedPosition;
        private Dictionary<Vector3, GameObject> _ColliderCubes;
        private bool _ScheduledRecalculation;

        public GameObject CollisionCubeObject;
        public Transform AttachedTransform;
        private int _Radius;

        public Mesh Mesh { get; private set; }
        public Bounds BoundingBox { get; private set; }

        public int Radius
        {
            get => _Radius;
            set
            {
                if (_Radius == value)
                {
                    return;
                }

                _Radius = value;
                _ScheduledRecalculation = true;
            }
        }


        public event EventHandler<Mesh> UpdatedMesh;

        private void Awake()
        {
            _SelfTransform = transform;
            _ColliderCubes = new Dictionary<Vector3, GameObject>();
            _LocalCacheQueue = new Queue<Vector3>();
        }

        private void Start()
        {
            WorldController.Current.ChunkMeshChanged += (sender, bounds) => { _ScheduledRecalculation = true; };
        }

        private void Update()
        {
            if (AttachedTransform == default)
            {
                return;
            }

            Vector3 difference = (_LastCalculatedPosition - AttachedTransform.position).Abs();

            if (_ScheduledRecalculation || difference.AnyGreaterThanOrEqual(Vector3.one))
            {
                _LastCalculatedPosition = AttachedTransform.position.Floor();

                Recalculate();
            }
        }

        private void OnDestroy()
        {
            Destroy(Mesh);
        }

        private void Recalculate()
        {
            RecalculateBoundingBox();
            TryCalculateLocalMeshData();
            CullOutOfBoundsSurfaces();

            //UpdatedMesh?.Invoke(this, Mesh);
            _ScheduledRecalculation = false;
        }

        private void RecalculateBoundingBox()
        {
            // +1 to include center blocks / position
            int size = (Radius * 2) + 1;

            BoundingBox = new Bounds(_LastCalculatedPosition, new Vector3(size, size, size));
        }

        private void TryCalculateLocalMeshData()
        {
            for (int x = -Radius; x < (Radius + 1); x++)
            {
                for (int y = -Radius; y < (Radius + 1); y++)
                {
                    for (int z = -Radius; z < (Radius + 1); z++)
                    {
                        Vector3 localPosition = new Vector3(x, y, z);
                        Vector3 globalPosition = _LastCalculatedPosition + localPosition;
                        Vector3 trueCenterGlobalPosition = globalPosition + Mathv.Half;

                        if (!WorldController.Current.TryGetBlockAt(globalPosition, out Block block)
                            || (block.Id == BlockController.BLOCK_EMPTY_ID)
                            || !block.HasAnyFaces())
                        {
                            if (_ColliderCubes.ContainsKey(trueCenterGlobalPosition))
                            {
                                _LocalCacheQueue.Enqueue(trueCenterGlobalPosition);
                            }

                            continue;
                        }

                        if (_ColliderCubes.ContainsKey(trueCenterGlobalPosition))
                        {
                            continue;
                        }

                        GameObject collisionCube = GetCollisionCube(trueCenterGlobalPosition);
                        _ColliderCubes.Add(trueCenterGlobalPosition, collisionCube);
                    }
                }
            }
        }

        private Queue<Vector3> _LocalCacheQueue;

        private void CullOutOfBoundsSurfaces()
        {
            foreach (Vector3 position in _ColliderCubes.Keys.Where(position => !BoundingBox.Contains(position)))
            {
                _LocalCacheQueue.Enqueue(position);
            }

            while (_LocalCacheQueue.Count > 0)
            {
                Vector3 position = _LocalCacheQueue.Dequeue();
                GameObject obj = _ColliderCubes[position];
                _ColliderCubes.Remove(position);
                ColliderCubeCache.CacheItem(ref obj);
            }
        }

        private GameObject GetCollisionCube(Vector3 position)
        {
            GameObject surfaceCollider = ColliderCubeCache.RetrieveItem() ?? Instantiate(CollisionCubeObject);
            Transform surfaceColliderTransform = surfaceCollider.transform;
            surfaceCollider.transform.parent = _SelfTransform;
            surfaceColliderTransform.position = position;
            surfaceCollider.SetActive(true);

            return surfaceCollider;
        }
    }
}