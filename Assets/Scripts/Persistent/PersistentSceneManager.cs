using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BenScr.CubeDash
{
    public enum SceneType
    {
        Persistent = 0,
        Menu = 1,
        Game = 2,
        Editor = 3,
        Debug = 4
    }

    public class PersistentSceneManager : MonoBehaviour
    {
        public const string PERSISTENT_SCENE = "Persistent";
        public const string MENU_SCENE = "Menu";
        public const string GAME_SCENE = "Game";
        public const string EDITOR_SCENE = "Editor";

        public static Action OnLoadScene;
        public static Action<SceneType> BeforeUnloadScene;
        public static SceneType ActiveScene = SceneType.Persistent;
        public static SceneType LastActiveScene = SceneType.Persistent;

        private void Awake()
        {
            Application.targetFrameRate = 144;
            PersistentObjectPool.Initialize(transform);
            OnInit();
        }

        private async void OnInit()
        {
            try
            {
                if (!IsSceneLoaded(MENU_SCENE))
                    await LoadSceneAsyncAdditive(MENU_SCENE);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to initialize the menu scene: {ex.Message}");
            }
        }


        public static async Task UnLoadAndLoadScene(SceneType unload, SceneType load)
        {
            if (unload == load)
                return;

            await LoadSceneAsyncAdditive(load.ToString());

            BeforeUnloadScene?.Invoke(unload);

            if (IsSceneLoaded(unload.ToString()))
            {
                AsyncOperation unloadOperation = SceneManager.UnloadSceneAsync(unload.ToString());
                if (unloadOperation != null)
                    await unloadOperation;
            }

            LastActiveScene = unload;
            ActiveScene = load;
            OnLoadScene?.Invoke();
        }

        public static async Task LoadSceneAsyncAdditive(string name)
        {
            if (!IsSceneLoaded(name))
                await SceneManager.LoadSceneAsync(name, LoadSceneMode.Additive);

            Scene scene = SceneManager.GetSceneByName(name);
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException($"Scene '{name}' could not be loaded.");

            SceneManager.SetActiveScene(scene);
        }

        public static bool Check()
        {
            if (!IsSceneLoaded(PERSISTENT_SCENE))
            {
                SceneManager.LoadScene(PERSISTENT_SCENE);
                return false;
            }

            return true;
        }

        internal static bool IsSceneLoaded(string name)
        {
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name == name)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
