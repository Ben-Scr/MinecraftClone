using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WorldGenerator : MonoBehaviour
{
    public Material blockMaterial;
    public Block[] blockTypes;


    [SerializeField] private int seed = 0;
    public bool addColliders = false;
    public bool addTrees = true;
    [SerializeField] private int viewDistance = 5;
    public float noiseScale = 20.0f;
    public float noiseHeight = 10.0f;
    public int groundOffset = 10;
    internal Vector2 noiseOffset;
    [SerializeField] private float chunkUpdateThreshold = 1.0f;


    private Dictionary<Vector3Int, Chunk> chunks = new Dictionary<Vector3Int, Chunk>();
    private HashSet<Chunk> lastActiveChunks = new HashSet<Chunk>();
    private Queue<Vector3Int> chunksToCreate = new Queue<Vector3Int>();
    private Vector3 lastChunkUpdatePlayerPosition;
    [SerializeField] private int maxChunksCreatedPerFrame = 2;

    public static WorldGenerator instance;

    private void Awake()
    {
        int resolution = 16;

        Chunk.TEXTURE_BLOCKS_COLS = (int)(1f / blockMaterial.mainTexture.texelSize.x / resolution) + 1;
        Chunk.TEXTURE_BLOCKS_ROWS = (int)(1f / blockMaterial.mainTexture.texelSize.y / resolution) + 1;
        Chunk.BLOCK_W = 1f / Chunk.TEXTURE_BLOCKS_COLS;
        Chunk.BLOCK_H = 1f / Chunk.TEXTURE_BLOCKS_ROWS;

        Debug.Log(Chunk.TEXTURE_BLOCKS_COLS + " cols, " + Chunk.TEXTURE_BLOCKS_ROWS + " rows.");
        instance = this;
    }

    void Start()
    {
        UnityEngine.Random.InitState(seed);
        noiseOffset = new Vector2(UnityEngine.Random.Range(-100000f, 100000f), UnityEngine.Random.Range(-100000f, 100000f));

        //for (int x = -viewDistance; x < viewDistance; x++)
        //{
        //    for (int z = -viewDistance; z < viewDistance; z++)
        //    {
        //        Chunk chunk = new Chunk(x, z);
        //        chunk.Prepare();
        //        chunks.Add(chunk.coordinate, chunk);
        //    }
        //}

        //foreach (Chunk chunk in chunks.Values)
        //{
        //    chunk.Update();
        //}
    }

    public void Update()
    {
        UpdateChunks(PlayerController.instance.transform.position);
    }

    public void UpdateChunks(Vector3 playerPosition)
    {
        bool playerMoveTheshold = Vector3.Distance(playerPosition, lastChunkUpdatePlayerPosition) < chunkUpdateThreshold;

        if (playerMoveTheshold && chunksToCreate.Count == 0)
            return;

        Vector3Int playerChunkCoordinate = GetChunkCoordinateFromPosition(playerPosition);

        HashSet<Chunk> currentActiveChunks = new HashSet<Chunk>(viewDistance * viewDistance);

        // Add new chunks
        for (int x = -viewDistance; x < viewDistance; x++)
        {
            for (int z = -viewDistance; z < viewDistance; z++)
            {
                int chunkX = playerChunkCoordinate.x + x;
                int chunkZ = playerChunkCoordinate.z + z;

                Chunk targetChunk = GetChunkByCoordinate(chunkX, chunkZ);

                if (targetChunk == null)
                {
                    Vector3Int coordinate = new Vector3Int(chunkX, 0, chunkZ);
                    if (!chunksToCreate.Contains(coordinate))
                        chunksToCreate.Enqueue(coordinate);
                }
                else
                {
                    currentActiveChunks.Add(targetChunk);

                    if (addColliders && targetChunk.meshCollider == null && Vector2.Distance(new Vector2(playerPosition.x, playerPosition.z), new Vector2(targetChunk.position.x, targetChunk.position.z)) <= Chunk.CHUNK_SIZE + 5)
                    {
                        targetChunk.AddMeshCollider();
                    }
                }
            }
        }

        for (int i = 0; i < Math.Min(chunksToCreate.Count, maxChunksCreatedPerFrame); i++)
        {
            Vector3Int coordinate = chunksToCreate.Dequeue();
            Chunk targetChunk = new Chunk(coordinate.x, coordinate.z);
            targetChunk.Prepare();
            chunks.Add(targetChunk.coordinate, targetChunk);
            targetChunk.Update();
            currentActiveChunks.Add(targetChunk);
        }

        if (playerMoveTheshold)
        {
            // Deactivate chunks out of sight
            foreach (Chunk chunk in lastActiveChunks)
            {
                bool visible = Vector3Int.Distance(playerChunkCoordinate, chunk.coordinate) < viewDistance;
                chunk.gameObject.SetActive(visible);
            }

            lastActiveChunks = currentActiveChunks;
        }

        currentActiveChunks.Clear();
        lastChunkUpdatePlayerPosition = playerPosition;
    }

    public int GetBlockAtPosition(Vector3 position)
    {
        Chunk chunk = GetChunkByPosition(position);

        if (chunk != null)
        {
            return chunk.GetBlockAtPosition(position - chunk.position);
        }

        return Chunk.BLOCK_AIR;
    }

    public Chunk GetChunkByCoordinate(int chunkX, int chunkZ)
    {
        if (chunks.TryGetValue(new Vector3Int(chunkX, 0, chunkZ), out Chunk chunk))
        {
            return chunk;
        }

        return null;
    }

    Vector3Int GetChunkCoordinateFromPosition(Vector3 position)
    {
        int chunkX = Mathf.FloorToInt(position.x / Chunk.CHUNK_SIZE);
        int chunkZ = Mathf.FloorToInt(position.z / Chunk.CHUNK_SIZE);

        return new Vector3Int(chunkX, 0, chunkZ);
    }

    Chunk GetChunkByPosition(Vector3 position)
    {
        Vector3Int coordinate = GetChunkCoordinateFromPosition(position);

        if (chunks.TryGetValue(new Vector3Int(coordinate.x, coordinate.y, coordinate.z), out Chunk chunk))
        {
            return chunk;
        }

        return null;
    }

    public void SetBlock(Vector3 position, int id)
    {
        Chunk chunk = GetChunkByPosition(position);

        if (chunk != null)
        {
            chunk.SetBlock(position - chunk.position, id);
        }
        else
        {
            Debug.LogWarning("Position is outside of world: " + position);
        }
    }

}

[Serializable]
public class BlockType
{
    public string name;
    public int id;

    public int backTexture;
    public int frontTexture;
    public int topTexture;
    public int bottomTexture;
    public int leftTexture;
    public int rightTexture;

    public int GetTexture(int face)
    {
        switch (face)
        {
            case 0:
                return backTexture;
            case 1:
                return frontTexture;
            case 2:
                return topTexture;
            case 3:
                return bottomTexture;
            case 4:
                return leftTexture;
            case 5:
                return rightTexture;
            default:
                Debug.LogWarning("Invalid face: " + face);
                return backTexture;
        }
    }

}