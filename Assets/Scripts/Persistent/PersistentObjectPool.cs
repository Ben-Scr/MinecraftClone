using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BenScr.CubeDash
{
    /// <summary>
    /// Owns inactive pooled Unity objects in the scene that remains loaded for the
    /// lifetime of the application. World-scoped managers can come and go without
    /// taking their released objects with them.
    /// </summary>
    public sealed class PersistentObjectPool : MonoBehaviour
    {
        private const string RootName = "Persistent Object Pools";

        private static PersistentObjectPool instance;
        private readonly Dictionary<string, Transform> categoryRoots = new();

        public static void Initialize(Transform persistentSceneParent)
        {
            if (instance != null)
            {
                if (persistentSceneParent != null && instance.transform.parent != persistentSceneParent)
                    MoveToParent(instance.gameObject, persistentSceneParent, false);
                return;
            }

            instance = FindFirstObjectByType<PersistentObjectPool>();
            if (instance != null)
            {
                if (persistentSceneParent != null && instance.transform.parent != persistentSceneParent)
                    MoveToParent(instance.gameObject, persistentSceneParent, false);
                return;
            }

            var poolObject = new GameObject(RootName);
            if (persistentSceneParent != null)
                poolObject.transform.SetParent(persistentSceneParent, false);
            else
                MoveToPersistentSceneIfAvailable(poolObject);

            instance = poolObject.AddComponent<PersistentObjectPool>();
        }

        public static Transform GetRoot(string category)
        {
            EnsureInitialized();
            return instance.GetOrCreateCategoryRoot(category);
        }

        public static void Store(GameObject pooledObject, string category)
        {
            if (pooledObject == null)
                return;

            pooledObject.SetActive(false);
            MoveToParent(pooledObject, GetRoot(category), false);
        }

        public static void MoveToParent(GameObject pooledObject, Transform parent, bool worldPositionStays)
        {
            if (pooledObject == null)
                return;

            Transform pooledTransform = pooledObject.transform;
            pooledTransform.SetParent(null, true);

            if (parent != null)
            {
                Scene targetScene = parent.gameObject.scene;
                if (targetScene.IsValid() && pooledObject.scene != targetScene)
                    SceneManager.MoveGameObjectToScene(pooledObject, targetScene);
            }

            pooledTransform.SetParent(parent, worldPositionStays);
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
        }

        private void OnDestroy()
        {
            if (instance == this)
                instance = null;

            categoryRoots.Clear();
        }

        private Transform GetOrCreateCategoryRoot(string category)
        {
            string rootName = string.IsNullOrWhiteSpace(category) ? "Default" : category;
            if (categoryRoots.TryGetValue(rootName, out Transform root) && root != null)
                return root;

            var rootObject = new GameObject(rootName);
            root = rootObject.transform;
            root.SetParent(transform, false);
            categoryRoots[rootName] = root;
            return root;
        }

        private static void EnsureInitialized()
        {
            if (instance != null)
                return;

            Initialize(null);
        }

        private static void MoveToPersistentSceneIfAvailable(GameObject poolObject)
        {
            Scene persistentScene = SceneManager.GetSceneByName(PersistentSceneManager.PERSISTENT_SCENE);
            if (persistentScene.IsValid() && persistentScene.isLoaded)
                SceneManager.MoveGameObjectToScene(poolObject, persistentScene);
        }
    }
}
