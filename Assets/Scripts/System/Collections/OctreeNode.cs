#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Mathematics;

#endregion

namespace Wyd.System.Collections
{
    public class OctreeNode<T> where T : unmanaged
    {
        private static readonly float3[] _coordinates =
        {
            new float3(-1, -1, -1),
            new float3(1, -1, -1),
            new float3(-1, -1, 1),
            new float3(1, -1, 1),

            new float3(-1, 1, -1),
            new float3(1, 1, -1),
            new float3(-1, 1, 1),
            new float3(1, 1, 1)
        };


        private readonly List<OctreeNode<T>> _Nodes;
        private readonly CubicVolume _Volume;

        public T Value { get; private set; }

        public bool IsUniform => _Nodes.Count == 0;
        public CubicVolume Volume => _Volume;

        public OctreeNode(float3 centerPoint, float size, T value)
        {
            _Nodes = new List<OctreeNode<T>>();
            _Volume = new CubicVolume(centerPoint, size);
            Value = value;
        }

        private void Collapse()
        {
            if (IsUniform)
            {
                return;
            }

            Value = _Nodes[0].Value;
            _Nodes.Clear();
        }

        private void Populate()
        {
            _Nodes.InsertRange(0, GetNodePopulation());
        }

        private IEnumerable<OctreeNode<T>> GetNodePopulation()
        {
            float3 offset = new float3(_Volume.Extent / 2f);

            yield return new OctreeNode<T>(_Volume.CenterPoint + (_coordinates[0] * offset), _Volume.Extent, Value);
            yield return new OctreeNode<T>(_Volume.CenterPoint + (_coordinates[1] * offset), _Volume.Extent, Value);
            yield return new OctreeNode<T>(_Volume.CenterPoint + (_coordinates[2] * offset), _Volume.Extent, Value);
            yield return new OctreeNode<T>(_Volume.CenterPoint + (_coordinates[3] * offset), _Volume.Extent, Value);
            yield return new OctreeNode<T>(_Volume.CenterPoint + (_coordinates[4] * offset), _Volume.Extent, Value);
            yield return new OctreeNode<T>(_Volume.CenterPoint + (_coordinates[5] * offset), _Volume.Extent, Value);
            yield return new OctreeNode<T>(_Volume.CenterPoint + (_coordinates[6] * offset), _Volume.Extent, Value);
            yield return new OctreeNode<T>(_Volume.CenterPoint + (_coordinates[7] * offset), _Volume.Extent, Value);
        }


        #region Checked Data Operations

        public T GetPoint(float3 point, bool hasChecked = false)
        {
            if (!hasChecked && !Volume.ContainsMinBiased(point))
            {
                throw new ArgumentOutOfRangeException(
                    $"Attempted to get point {point} in {nameof(OctreeNode<T>)}, but {nameof(_Volume)} does not contain it.\r\n"
                    + $"State Information: [Volume {_Volume}], [{nameof(IsUniform)} {IsUniform}], [Branches {_Nodes.Count}], "
                    + (_Nodes.Count > 0
                        ? $"[Branch Values {string.Join(", ", _Nodes.Select(node => node.Value))}"
                        : string.Empty));
            }


            if (!IsUniform)
            {
                int octant = DetermineOctant(point);

                if (!hasChecked && (octant >= _Nodes.Count))
                {
                    throw new ArgumentOutOfRangeException(
                        $"Attempted to step into octant of {nameof(OctreeNode<T>)} and failed ({nameof(GetPoint)}).\r\n"
                        + $"State Information: [Volume {_Volume}], [{nameof(IsUniform)} {IsUniform}], [Branches {_Nodes.Count}], "
                        + (_Nodes.Count > 0
                            ? $"[Branch Values {string.Join(", ", _Nodes.Select(node => node.Value))}]"
                            : string.Empty)
                        + $"[Octant {octant}]");
                }

                return _Nodes[octant].GetPoint(point, true);
            }
            else
            {
                return Value;
            }
        }

        public void SetPoint(float3 point, T newValue, bool hasChecked = false)
        {
            if (!hasChecked && !Volume.ContainsMinBiased(point))
            {
                throw new ArgumentOutOfRangeException(
                    $"Attempted to set point {point} in {nameof(OctreeNode<T>)}, but {nameof(_Volume)} does not contain it.\r\n"
                    + $"State Information: [Volume {_Volume}], [{nameof(IsUniform)} {IsUniform}], [Branches {_Nodes.Count}], "
                    + (_Nodes.Count > 0
                        ? $"[Branch Values {string.Join(", ", _Nodes.Select(node => node.Value))}"
                        : string.Empty));
            }

            if (IsUniform && (Value.GetHashCode() == newValue.GetHashCode()))
            {
                // operation does nothing, so return
                return;
            }

            if (_Volume.Size <= 1f)
            {
                // reached smallest possible depth (usually 1x1x1) so
                // set value and return
                Value = newValue;
                return;
            }

            if (IsUniform)
            {
                // node has no child nodes to traverse, so populate
                Populate();
            }

            int octant = DetermineOctant(point);

            if (!hasChecked && (octant >= _Nodes.Count))
            {
                throw new ArgumentOutOfRangeException(
                    $"Attempted to step into octant of {nameof(OctreeNode<T>)} and failed ({nameof(SetPoint)}).\r\n"
                    + $"State Information: [Volume {_Volume}], [{nameof(IsUniform)} {IsUniform}], [Branches {_Nodes.Count}], "
                    + (_Nodes.Count > 0
                        ? $"[Branch Values {string.Join(", ", _Nodes.Select(node => node.Value))}]"
                        : string.Empty)
                    + $"[Octant {octant}]");
            }

            // recursively dig into octree and set
            _Nodes[octant].SetPoint(point, newValue, true);

            // on each recursion back-step, ensure integrity of node
            // and collapse if all child node values are equal
            if (!IsUniform && CheckShouldCollapse())
            {
                Collapse();
            }
        }

        private bool CheckShouldCollapse()
        {
            if (IsUniform)
            {
                throw new ArgumentOutOfRangeException(
                    $"Attempted to check for required collapsing of {nameof(OctreeNode<T>)} and failed..\r\n"
                    + $"State Information: [Volume {_Volume}], [{nameof(IsUniform)} {IsUniform}], [Branches {_Nodes.Count}], "
                    + (_Nodes.Count > 0
                        ? $"[Branch Values {string.Join(", ", _Nodes.Select(node => node.Value))}]"
                        : string.Empty));
            }

            T firstValue = _Nodes[0].Value;

            // avoiding using linq here for performance sensitivity
            for (int index = 0; index < 8 /* octants! */; index++)
            {
                OctreeNode<T> node = _Nodes[index];

                if (!node.IsUniform || (node.Value.GetHashCode() != firstValue.GetHashCode()))
                {
                    return false;
                }
            }

            return true;
        }

        public IEnumerable<T> GetAllData()
        {
            for (int index = 0; index < WydMath.Product(_Volume.Size); index++)
            {
                yield return GetPoint(_Volume.MinPoint + WydMath.IndexTo3D(index, (int)_Volume.Size), true);
            }
        }

        #endregion


        #region Try .. Data Operations

        public bool TryGetPoint(float3 point, out T value)
        {
            value = default;

            if (!Volume.ContainsMinBiased(point))
            {
                return false;
            }
            else if (!IsUniform)
            {
                int octant = DetermineOctant(point);
                return _Nodes[octant].TryGetPoint(point, out value);
            }
            else
            {
                value = Value;
                return true;
            }
        }

        #endregion


        #region Helper Methods

        // indexes:
        // bottom half quadrant:
        // 1 3
        // 0 2
        // top half quadrant:
        // 5 7
        // 4 6
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DetermineOctant(float3 point)
        {
            bool3 result = point < _Volume.CenterPoint;
            return (result[0] ? 0 : 1) + (result[1] ? 0 : 4) + (result[2] ? 0 : 2);
        }

        #endregion
    }
}
