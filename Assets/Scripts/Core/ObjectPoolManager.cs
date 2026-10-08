using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Static pooling API backed by the scene's Pooler instance, whose Awake creates the pools:
/// spawning before it runs throws. Pooled objects return themselves.
/// </summary>
public class ObjectPoolManager : MonoBehaviour
{
    public enum PoolType
    {
        ParticleSystems,
        GameObjects,
        SoundFX
    }

    [SerializeField] private bool _addToDontDestroyOnLoad = true;

    private static GameObject _particleSystemsEmpty;
    private static GameObject _gameObjectsEmpty;
    private static GameObject _soundFXEmpty;

    private static Dictionary<GameObject, ObjectPool<GameObject>> _objectPools;
    private static Dictionary<GameObject, GameObject> _cloneToPrefabMap;

    private void Awake()
    {
        _objectPools = new Dictionary<GameObject, ObjectPool<GameObject>>();
        _cloneToPrefabMap = new Dictionary<GameObject, GameObject>();
        SetupEmpties();
    }

    private void SetupEmpties()
    {
        GameObject holder = new("Pooled Objects");

        _particleSystemsEmpty = new GameObject("Particle Systems");
        _particleSystemsEmpty.transform.SetParent(holder.transform);

        _gameObjectsEmpty = new GameObject("Game Objects");
        _gameObjectsEmpty.transform.SetParent(holder.transform);

        _soundFXEmpty = new GameObject("Sound FX");
        _soundFXEmpty.transform.SetParent(holder.transform);

        if (_addToDontDestroyOnLoad) DontDestroyOnLoad(holder);
    }

    public static T SpawnObject<T>(T typePrefab, Vector3 spawnPos, Quaternion spawnRotation, PoolType poolType = PoolType.GameObjects) where T : Component
        => SpawnObject<T>(typePrefab.gameObject, spawnPos, spawnRotation, poolType);

    public static GameObject SpawnObject(GameObject objectToSpawn, Vector3 spawnPos, Quaternion spawnRotation, PoolType poolType = PoolType.GameObjects)
        => SpawnObject<GameObject>(objectToSpawn, spawnPos, spawnRotation, poolType);

    public static void ReturnObjectToPool(GameObject obj, PoolType poolType = PoolType.GameObjects)
    {
        if (!_cloneToPrefabMap.TryGetValue(obj, out GameObject prefab))
        {
            Debug.LogWarning($"Trying to return an object that is not pooled: {obj.name}");
            return;
        }

        Transform parent = ParentFor(poolType).transform;
        if (obj.transform.parent != parent) obj.transform.SetParent(parent);

        if (_objectPools.TryGetValue(prefab, out ObjectPool<GameObject> pool)) pool.Release(obj);
    }

    private static T SpawnObject<T>(GameObject objectToSpawn, Vector3 spawnPos, Quaternion spawnRotation, PoolType poolType) where T : Object
    {
        if (!_objectPools.ContainsKey(objectToSpawn)) CreatePool(objectToSpawn, spawnPos, spawnRotation, poolType);

        GameObject obj = _objectPools[objectToSpawn].Get();
        if (obj == null) return null;

        _cloneToPrefabMap.TryAdd(obj, objectToSpawn);
        obj.transform.SetPositionAndRotation(spawnPos, spawnRotation);
        obj.SetActive(true);

        if (typeof(T) == typeof(GameObject)) return obj as T;

        T component = obj.GetComponent<T>();
        if (component == null) Debug.LogError($"Object {objectToSpawn.name} doesn't have component of type {typeof(T)}");
        return component;
    }

    private static void CreatePool(GameObject prefab, Vector3 pos, Quaternion rot, PoolType poolType)
    {
        ObjectPool<GameObject> pool = new(
            createFunc: () => CreateObject(prefab, pos, rot, poolType),
            actionOnRelease: obj => obj.SetActive(false),
            actionOnDestroy: obj => _cloneToPrefabMap.Remove(obj));

        _objectPools.Add(prefab, pool);
    }

    private static GameObject CreateObject(GameObject prefab, Vector3 pos, Quaternion rot, PoolType poolType)
    {
        // Instantiated inactive, so its Awake/OnEnable don't run before the pool hands it out.
        prefab.SetActive(false);
        GameObject obj = Instantiate(prefab, pos, rot);
        prefab.SetActive(true);

        obj.transform.SetParent(ParentFor(poolType).transform);
        return obj;
    }

    private static GameObject ParentFor(PoolType poolType) => poolType switch
    {
        PoolType.ParticleSystems => _particleSystemsEmpty,
        PoolType.SoundFX => _soundFXEmpty,
        _ => _gameObjectsEmpty,
    };
}
