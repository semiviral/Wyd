#region

using System;
using System.Collections.Generic;
using UnityEngine;

#endregion

namespace Game.Entities
{
    public interface IEntity
    {
        Transform Transform { get; }
        Rigidbody Rigidbody { get; }
        Vector3 CurrentChunk { get; }
        IReadOnlyList<string> Tags { get; }

        event EventHandler<Vector3> CausedPositionChanged;
        event EventHandler<Vector3> ChunkPositionChanged;
        event EventHandler<IEntity> EntityDestroyed;
    }
}