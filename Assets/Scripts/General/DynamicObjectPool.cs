using System.Collections.Generic;
using BenScr.CubeDash;
using UnityEngine;

public class DynamicObjectPool<TKey>
{
    private const int DefaultCapacityPerKey = 128;
    private static readonly Dictionary<TKey, Stack<GameObject>> Pools = new();
    private static readonly string PoolCategory = $"Dynamic {typeof(TKey).Name} Pools";

    public int Count(TKey key)
    {
        return Pools.TryGetValue(key, out Stack<GameObject> stack)
            ? RemoveDestroyedEntries(stack)
            : 0;
    }

    public void PreWarm(TKey key, GameObject prefab, int count)
    {
        PreWarm(key, prefab, count, DefaultCapacityPerKey);
    }

    public void PreWarm(TKey key, GameObject prefab, int count, int maxCapacity)
    {
        if (prefab == null || count <= 0 || maxCapacity <= 0)
            return;

        Stack<GameObject> stack = GetOrCreateStack(key, Mathf.Min(count, maxCapacity));
        int targetCount = Mathf.Min(count, maxCapacity);
        RemoveDestroyedEntries(stack);
        TrimToCapacity(stack, maxCapacity);
        int liveCount = stack.Count;
        Transform root = PersistentObjectPool.GetRoot(PoolCategory);

        for (int i = liveCount; i < targetCount; i++)
        {
            GameObject go = Object.Instantiate(prefab, root);
            go.SetActive(false);
            stack.Push(go);
        }
    }

    public GameObject Get(TKey key, GameObject prefab, Vector3 pos, Quaternion rot)
    {
        if (TryTake(key, out GameObject obj))
        {
            obj.transform.SetPositionAndRotation(pos, rot);
            obj.SetActive(true);
            return obj;
        }

        Transform root = PersistentObjectPool.GetRoot(PoolCategory);
        return Object.Instantiate(prefab, pos, rot, root);
    }

    public GameObject Get(TKey key, GameObject prefab, Transform parent, bool worldPositionStays = true)
    {
        if (TryTake(key, out GameObject obj))
        {
            PersistentObjectPool.MoveToParent(obj, parent, worldPositionStays);
            obj.SetActive(true);
            return obj;
        }

        return Object.Instantiate(prefab, parent);
    }

    public GameObject Get(TKey key, GameObject prefab, RectTransform rect, Transform parent)
    {
        if (TryTake(key, out GameObject obj))
        {
            PersistentObjectPool.MoveToParent(obj, parent, true);
            obj.SetActive(true);
            return obj;
        }

        var newObj = Object.Instantiate(prefab, rect);
        newObj.transform.SetParent(parent, true);
        return newObj;
    }

    public bool Release(TKey key, GameObject obj)
    {
        return Release(key, obj, DefaultCapacityPerKey);
    }

    public bool Release(TKey key, GameObject obj, int maxCapacity)
    {
        if (obj == null)
            return false;

        if (maxCapacity <= 0)
        {
            DestroyObject(obj);
            return false;
        }

        Stack<GameObject> stack = GetOrCreateStack(key);
        while (stack.Count > 0 && stack.Peek() == null)
            stack.Pop();

        TrimToCapacity(stack, maxCapacity);
        if (stack.Count >= maxCapacity)
        {
            DestroyObject(obj);
            return false;
        }

        PersistentObjectPool.Store(obj, PoolCategory);
        stack.Push(obj);
        return true;
    }

    public void Clear(TKey key)
    {
        if (!Pools.TryGetValue(key, out Stack<GameObject> stack))
            return;

        while (stack.Count > 0)
            DestroyObject(stack.Pop());

        Pools.Remove(key);
    }

    private static Stack<GameObject> GetOrCreateStack(TKey key, int capacity = 0)
    {
        if (Pools.TryGetValue(key, out Stack<GameObject> stack))
            return stack;

        stack = capacity > 0 ? new Stack<GameObject>(capacity) : new Stack<GameObject>();
        Pools[key] = stack;
        return stack;
    }

    private static bool TryTake(TKey key, out GameObject obj)
    {
        obj = null;
        if (!Pools.TryGetValue(key, out Stack<GameObject> stack))
            return false;

        while (stack.Count > 0)
        {
            GameObject candidate = stack.Pop();
            if (candidate == null)
                continue;

            obj = candidate;
            return true;
        }

        return false;
    }

    private static int RemoveDestroyedEntries(Stack<GameObject> stack)
    {
        if (stack.Count == 0)
            return 0;

        var liveObjects = new Stack<GameObject>(stack.Count);
        while (stack.Count > 0)
        {
            GameObject candidate = stack.Pop();
            if (candidate != null)
                liveObjects.Push(candidate);
        }

        while (liveObjects.Count > 0)
            stack.Push(liveObjects.Pop());

        return stack.Count;
    }

    private static void TrimToCapacity(Stack<GameObject> stack, int maxCapacity)
    {
        while (stack.Count > maxCapacity)
            DestroyObject(stack.Pop());
    }

    private static void DestroyObject(GameObject obj)
    {
        if (obj == null)
            return;

        if (Application.isPlaying)
            Object.Destroy(obj);
        else
            Object.DestroyImmediate(obj);
    }
}
