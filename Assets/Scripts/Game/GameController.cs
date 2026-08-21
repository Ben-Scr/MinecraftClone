using BenScr.CubeDash;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BenScr.MinecraftClone
{
    public class GameController : MonoBehaviour
    {
        private const float DebugHudRefreshInterval = 0.2f;
        private static HashSet<FreezeReason> freezeReasons = new HashSet<FreezeReason>();

        public static bool IsFrozen =>
            freezeReasons.Contains(FreezeReason.BGScreen) ||
            freezeReasons.Contains(FreezeReason.LoadingTerrain);

        public static bool IsPlayerFrozen => IsFrozen || freezeReasons.Contains(FreezeReason.ManualCamera);

        [SerializeField] private TextMeshProUGUI fpsTxt;
        [SerializeField] private TextMeshProUGUI playerPosTxt;
        [SerializeField] private int targetFPS = -1;
        [SerializeField] private PlayerController player;
        [SerializeField] private GameObject loadingTerrainScreen;
        [SerializeField] private GameObject pauseScreen;
        [SerializeField] private GameObject playerUI;

        [SerializeField] private GameObject pauseGameScreens;

        private bool isLeavingGame;
        private float nextDebugHudRefreshTime;

        public static Action<FreezeReason> OnFreeze;
        public static Action<FreezeReason> OnUnFreeze;

        private void Awake()
        {
            Application.targetFrameRate = targetFPS < 0 ? 60 : targetFPS;
            freezeReasons.Clear();
            Freeze(FreezeReason.LoadingTerrain);

            if (loadingTerrainScreen != null)
                loadingTerrainScreen.SetActive(true);

            PersistentSceneManager.Check();
        }
        private void Update()
        {
            if (Time.unscaledTime >= nextDebugHudRefreshTime)
            {
                UpdateDebugHud();
                nextDebugHudRefreshTime = Time.unscaledTime + DebugHudRefreshInterval;
            }

            if (CanvasScreenManager.ActiveScreen?.activeInHierarchy ?? false)
                Freeze(FreezeReason.BGScreen);
            else
                Unfreeze(FreezeReason.BGScreen);

            if (IsFrozen) return;

            if (Input.GetKeyDown(KeyCode.R))
            {
                ReloadScene();
            }

            if (CanvasScreenManager.ActiveScreen == null && Input.GetKeyDown(KeyCode.Escape))
            {
                CanvasScreenManager.Instance.OpenScreen(pauseScreen);
            }
        }

        private void UpdateDebugHud()
        {
            if (fpsTxt != null)
            {
                float frameDuration = Mathf.Max(Time.unscaledDeltaTime, 0.000001f);
                fpsTxt.SetText("FPS: {0:0}", 1f / frameDuration);
            }

            if (playerPosTxt != null && player != null)
            {
                Vector3 playerPos = player.transform.position;
                playerPosTxt.SetText(
                    "X: {0:0} Y: {1:0} Z: {2:0}",
                    playerPos.x,
                    playerPos.y,
                    playerPos.z);
            }
        }

        private void ReloadScene()
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        public void ClosePauseScreen()
        {
            CanvasScreenManager.Instance.CloseActiveScreen();
        }
        public async void LoadMenuScene()
        {
            if (isLeavingGame)
                return;

            isLeavingGame = true;
            Freeze(FreezeReason.LoadingTerrain);

            if (loadingTerrainScreen != null)
                loadingTerrainScreen.SetActive(true);

            UnityEngine.Debug.Log("Saving world...");
            await System.Threading.Tasks.Task.Yield();

            SaveController.OperationResult saveResult;
            try
            {
                saveResult = await SaveController.TrySaveWorldAsync();
            }
            catch (Exception ex)
            {
                saveResult = SaveController.OperationResult.Failed(ex.Message);
            }

            if (!saveResult.Success)
            {
                UnityEngine.Debug.LogError(saveResult.Error);
                isLeavingGame = false;
                Unfreeze(FreezeReason.LoadingTerrain);

                if (loadingTerrainScreen != null)
                    loadingTerrainScreen.SetActive(false);

                return;
            }

            try
            {
                await PersistentSceneManager.UnLoadAndLoadScene(SceneType.Game, SceneType.Menu);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Failed to return to the menu: {ex.Message}");
                isLeavingGame = false;
                Unfreeze(FreezeReason.LoadingTerrain);

                if (loadingTerrainScreen != null)
                    loadingTerrainScreen.SetActive(false);
            }
        }

        private void OnApplicationQuit()
        {
            if (isLeavingGame)
                return;

            if (!SaveController.TrySaveWorld(out string error))
                UnityEngine.Debug.LogError(error);
        }

        public static void Freeze(FreezeReason reason)
        {
            if (!freezeReasons.Contains(reason))
            {
                freezeReasons.Add(reason);
                OnFreeze?.Invoke(reason);
            }
        }

        public static void Unfreeze(FreezeReason reason)
        {
            if (freezeReasons.Contains(reason))
            {
                freezeReasons.Remove(reason);
                OnUnFreeze?.Invoke(reason);
            }
        }

        private void OnEnable()
        {
            TerrainGenerator.OnLoadedTerrain += OnLoadedTerrain; 
        }
        private void OnDisable()
        {
            TerrainGenerator.OnLoadedTerrain -= OnLoadedTerrain;
        }

        private void OnLoadedTerrain()
        {
            Camera.main.gameObject.SetActive(false);
            player.gameObject.SetActive(true);
            playerUI.gameObject.SetActive(true);
            loadingTerrainScreen.gameObject.SetActive(false);
            RenderSettings.fog = true;
            Unfreeze(FreezeReason.LoadingTerrain);
        }
    }

    public enum FreezeReason
    {
        Pause,
        Dialogue,
        Cutscene,
        BGScreen,
        Inventory,
        ManualCamera,
        LoadingTerrain
    }
}
