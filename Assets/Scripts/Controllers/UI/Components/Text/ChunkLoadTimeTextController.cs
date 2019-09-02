#region

using System;
using System.Linq;
using Game.World.Chunk;
using TMPro;
using UnityEngine;

#endregion

namespace Controllers.UI.Components.Text
{
    public class ChunkLoadTimeTextController : MonoBehaviour
    {
        private const double _TOLERANCE = 0.001d;
        private TextMeshProUGUI _ChunkLoadTimeText;
        private double _LastBuildTime;
        private double _LastMeshTime;

        private void Awake()
        {
            _ChunkLoadTimeText = GetComponent<TextMeshProUGUI>();
        }

        private void Start()
        {
            (double buildTime, double meshTime) = CalculateBuildAndMeshTimes();

            UpdateChunkLoadTimeText(buildTime, meshTime);
        }

        private void Update()
        {
            (double buildTime, double meshTime) = CalculateBuildAndMeshTimes();

            if ((Math.Abs(buildTime - _LastBuildTime) > _TOLERANCE) ||
                (Math.Abs(meshTime - _LastMeshTime) > _TOLERANCE))
            {
                UpdateChunkLoadTimeText(buildTime, meshTime);
            }
        }

        private (double, double) CalculateBuildAndMeshTimes()
        {
            double avgBuildTime = Chunk.BuildTimes.Count > 0 ? Chunk.BuildTimes.Average() : 0d;
            double avgMeshTime = Chunk.MeshTimes.Count > 0 ? Chunk.MeshTimes.Average() : 0d;

            double buildTime = Math.Round(avgBuildTime, 0);
            double meshTime = Math.Round(avgMeshTime, 0);

            return (buildTime, meshTime);
        }

        private void UpdateChunkLoadTimeText(double buildTime, double meshTime)
        {
            _LastBuildTime = buildTime;
            _LastMeshTime = meshTime;

            _ChunkLoadTimeText.text = $"Chunk Load Time: (b{buildTime}ms, m{meshTime}ms)";
        }
    }
}