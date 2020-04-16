#region

using System;
using System.Collections.Generic;
using Serilog;
using Unity.Mathematics;
using Wyd.Game;
using Wyd.Game.World.Blocks;
using Wyd.Graphics;
using Wyd.System.Extensions;

#endregion

namespace Wyd.Controllers.State
{
    public class BlockController : SingletonController<BlockController>
    {
        public const ushort AIR_ID = 0;
        public Dictionary<string, ushort> BlockNames;
        public List<IBlockDefinition> BlockDefinitions;

        private Dictionary<BlockDefinition.Property, HashSet<ushort>> _PropertiesBuckets;

        private void Awake()
        {
            AssignSingletonInstance(this);

            BlockNames = new Dictionary<string, ushort>();
            BlockDefinitions = new List<IBlockDefinition>();

            InitializeBlockPropertiesBuckets();

            RegisterBlockDefinition("Air", Block.Types.None, null, BlockDefinition.Property.Transparent);
        }

        private void InitializeBlockPropertiesBuckets()
        {
            _PropertiesBuckets = new Dictionary<BlockDefinition.Property, HashSet<ushort>>();

            Log.Debug($"Initializing property buckets for all {nameof(BlockDefinition.Property)}s.");

            foreach (BlockDefinition.Property property in EnumExtensions.GetEnumsList<BlockDefinition.Property>())
            {
                _PropertiesBuckets.Add(property, new HashSet<ushort>());
            }
        }

        private void SortBlockDefinitionPropertiesToBuckets(BlockDefinition blockDefinition)
        {
            foreach (BlockDefinition.Property property in EnumExtensions.GetEnumsList<BlockDefinition.Property>())
            {
                if (blockDefinition.Properties.HasFlag(property))
                {
                    _PropertiesBuckets[property].Add(blockDefinition.Id);
                }
            }
        }

        public void RegisterBlockDefinition(string blockName, Block.Types type,
            Func<int3, Direction, string> uvsRule, params BlockDefinition.Property[] properties)
        {
            ushort assignedBlockId;

            try
            {
                assignedBlockId = (ushort)BlockDefinitions.Count;
            }
            catch (OverflowException)
            {
                Log.Error("BlockController has registered too many blocks and is out of valid block ids.");
                return;
            }

            if (uvsRule == default)
            {
                uvsRule = (position, direction) => blockName;
            }

            BlockDefinition blockDefinition =
                new BlockDefinition(assignedBlockId, blockName, type, uvsRule, properties);

            BlockDefinitions.Add(blockDefinition);
            BlockNames.Add(blockName, assignedBlockId);
            SortBlockDefinitionPropertiesToBuckets(blockDefinition);

            Log.Information($"Successfully added block `{blockName}` with ID: {assignedBlockId}");
        }

        public bool GetBlockSpriteUVs(ushort blockId, int3 position, Direction direction, float3 size2d,
            out BlockUVs blockUVs)
        {
            if (!BlockIdExists(blockId))
            {
                Log.Error(
                    $"Failed to return block sprite UVs for direction `{direction}` of block with id `{blockId}`: block id does not exist.");
                blockUVs = null;
                return false;
            }

            BlockDefinitions[blockId].EvaluateUVsRule(blockId, position, direction, out string textureName);
            if (!TextureController.Current.TryGetTextureId(textureName, out int textureId))
            {
                Log.Warning(
                    $"Failed to return block sprite UVs for direction `{direction}` of block with id `{blockId}`: texture does not exist for block.");
                blockUVs = null;
                return false;
            }

            blockUVs = new BlockUVs(new float3(0, 0, textureId), new float3(size2d.x, 0, textureId),
                new float3(0, size2d.z, textureId), new float3(size2d.x, size2d.z, textureId));

            Log.Verbose($"Block `{textureName}:{blockId}` returned block UVs `{blockUVs}`.");

            return true;
        }

        public bool BlockIdExists(ushort blockId) => blockId < BlockDefinitions.Count;

        public bool TryGetBlockId(string blockName, out ushort blockId)
        {
            if (!BlockNames.TryGetValue(blockName, out blockId))
            {
                blockId = 0;
                return false;
            }

            return true;
        }

        public bool TryGetBlockName(ushort blockId, out string blockName)
        {
            blockName = string.Empty;

            if (!BlockIdExists(blockId))
            {
                return false;
            }

            blockName = BlockDefinitions[blockId].BlockName;
            return true;
        }

        public IReadOnlyBlockDefinition GetBlockDefinition(ushort blockId)
        {
            if (BlockIdExists(blockId))
            {
                return BlockDefinitions[blockId];
            }

            Log.Error($"Failed to return block rule for block with id `{blockId}`: block does not exist.");
            return null;
        }

        public bool TryGetBlockDefinition(ushort blockId, out IReadOnlyBlockDefinition blockDefinition)
        {
            if (BlockIdExists(blockId))
            {
                blockDefinition = BlockDefinitions[blockId];
                return true;
            }

            Log.Error($"Failed to return block rule for block with id `{blockId}`: block does not exist.");

            blockDefinition = default;
            return false;
        }

        public bool CheckBlockHasProperties(ushort blockId, BlockDefinition.Property property) =>
            _PropertiesBuckets[property].Contains(blockId);
    }
}
