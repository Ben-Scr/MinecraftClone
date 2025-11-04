using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WorldGenerator : MonoBehaviour
{
    public Material blockMaterial;

    public BlockType[] blockTypes;

    public GameObject player;

    public int seed = 0;
    public bool addTrees = true;
    public int viewDistance = 5;
    public float noiseScale = 20.0f;
    public float noiseHeight = 10.0f;
    public int groundOffset = 10;
    internal int noiseOffset;

    List<Chunk> chunks = new List<Chunk>();

    void Start()
    {
        UnityEngine.Random.InitState(seed);
        noiseOffset = UnityEngine.Random.Range(-100000, 100000);

        for (int x = -viewDistance; x < viewDistance; x++)
        {
            for (int z = -viewDistance; z < viewDistance; z++)
            {
                Chunk chunk = new Chunk(x, z, this);
                chunk.Prepare();
                chunks.Add(chunk);
            }
        }

        foreach(Chunk chunk in chunks)
        {
            chunk.Update();
        }
    }

    private void Update()
    {
        Vector3Int playerChunkCoordinate = GetChunkCoordinateFromPosition(player.transform.position);

        // Add new chunks
        for (int x = -viewDistance; x < viewDistance; x++)
        {
            for (int z = -viewDistance; z < viewDistance; z++)
            {
                int chunkX = playerChunkCoordinate.x + x;
                int chunkZ = playerChunkCoordinate.z + z;

                Chunk targetChunk = GetChunkByCoordinate(chunkX, chunkZ);

                if(targetChunk == null)
                {
                    Chunk chunk = new Chunk(chunkX, chunkZ, this);
                    chunk.Prepare();
                    chunks.Add(chunk);

                    chunk.Update();
                }
            }
        }

        // Deactivate chunks out of sight
        foreach(Chunk chunk in chunks)
        {
            bool visible = Vector3Int.Distance(playerChunkCoordinate, chunk.coordinate) < viewDistance;
            chunk.gameObject.SetActive(visible);
        }
    }

    public int GetBlockAtPosition(Vector3 position)
    {
        Chunk chunk = GetChunkByPosition(position);

        if(chunk != null)
        {
            return chunk.GetBlockAtPosition(position - chunk.position);
        }

        return Chunk.BLOCK_AIR;
    }

    public Chunk GetChunkByCoordinate(int chunkX, int chunkZ)
    {
        foreach (Chunk chunk in chunks)
        {
            if (chunk.coordinate.x == chunkX && chunk.coordinate.z == chunkZ)
            {
                return chunk;
            }
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

        foreach (Chunk chunk in chunks)
        {
            if (chunk.coordinate.x == coordinate.x && chunk.coordinate.z == coordinate.z)
            {
                return chunk;
            }
        }

        return null;
    }

    public void SetBlock(Vector3 position, int id)
    {
        Chunk chunk = GetChunkByPosition(position);

        if(chunk != null)
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
        switch(face)
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