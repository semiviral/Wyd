#region

using System;
using Controllers.World;
using Game;
using Game.World.Blocks;
using NLog;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

#endregion

namespace Controllers.Game
{
    public class GameController : SingletonController<GameController>
    {
        private void Awake()
        {
            AssignCurrent(this);
            DontDestroyOnLoad(this);
            ToggleCursorLocked(true);
            QualitySettings.vSyncCount = 0;
        }

        private void Start()
        {
            RegisterDefaultBlocks();
        }

        private void OnApplicationQuit()
        {
            LogManager.Shutdown();
        }

        private void RegisterDefaultBlocks()
        {
            BlockController.Current.RegisterBlockRules("Bedrock", false);
            BlockController.Current.RegisterBlockRules("Grass", false, (position, direction) =>
            {
                Vector3 positionAbove = position + Vector3.up;
                Block block = WorldController.Current.GetBlockAt(positionAbove);

                if (!block.Transparent)
                {
                    return "Dirt";
                }

                switch (direction)
                {
                    // todo decide on whether to use this ??
//                    case Direction.North:
//                        string northCheck = worldController.GetBlockAtPosition(position + new Vector3Int(0, -1, 1));
//
//                        return northCheck.Equals("Grass") ? "Grass" : "Grass_Side";
//                    case Direction.East:
//                        string eastCheck = worldController.GetBlockAtPosition(position + new Vector3Int(1, -1, 0));
//
//                        return eastCheck.Equals("Grass") ? "Grass" : "Grass_Side";
//                    case Direction.South:
//                        string southCheck = worldController.GetBlockAtPosition(position + new Vector3Int(0, -1, -1));
//
//                        return southCheck.Equals("Grass") ? "Grass" : "Grass_Side";
//                    case Direction.West:
//                        string westCheck = worldController.GetBlockAtPosition(position + new Vector3Int(-1, -1, 0));
//
//                        return westCheck.Equals("Grass") ? "Grass" : "Grass_Side";

                    case Direction.North:
                    case Direction.East:
                    case Direction.South:
                    case Direction.West:
                        return "GrassSide";
                    case Direction.Up:
                        return "Grass";
                    case Direction.Down:
                        return "Dirt";
                    default:
                        throw new ArgumentOutOfRangeException(nameof(direction), direction, null);
                }
            });
            BlockController.Current.RegisterBlockRules("Dirt", false);
            BlockController.Current.RegisterBlockRules("Stone", false);
            BlockController.Current.RegisterBlockRules("Glass", true);
            BlockController.Current.RegisterBlockRules("CoalOre", false);
            BlockController.Current.RegisterBlockRules("GoldOre", false);
            BlockController.Current.RegisterBlockRules("DiamondOre", false);
        }

        public void ToggleCursorLocked(bool value)
        {
            if (value)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        public void QuitToMainMenu()
        {
            SceneManager.LoadSceneAsync("Scenes/MainMenu", LoadSceneMode.Single);
        }

        public static void ApplicationClose(int exitCode = -1)
        {
            Application.Quit(exitCode);

#if UNITY_EDITOR
            EditorApplication.ExitPlaymode();
#endif
        }
    }
}