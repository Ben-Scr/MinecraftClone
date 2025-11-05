using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerManager : MonoBehaviour
{
    private static readonly Vector3 HalfExtents = new Vector3(0.499f, 0.499f, 0.499f);

    [SerializeField] private float maxInteractionDistance = 5;

    public WorldGenerator world;

    public GameObject highlightBlock;

    Vector3 highlightPosition;
    Vector3 placeBlockPosition;

    bool highlightBlockVisible = false;


    public Block selectedBlock;

    void Update()
    {
        if (highlightBlockVisible)
        {
            if (Input.GetMouseButtonDown(0))
            {
                world.SetBlock(highlightPosition, Chunk.BLOCK_AIR);
            }

            if (Input.GetMouseButtonDown(1))
            {
                Vector3 center = placeBlockPosition + new Vector3(0.5f, 0.5f, 0.5f);
                bool overlapsWithPlayer = Physics.CheckBox(center, HalfExtents, Quaternion.identity, LayerMask.GetMask("Player"));

                if (!overlapsWithPlayer)
                {
                    world.SetBlock(placeBlockPosition, selectedBlock.id);
                }
            }
        }

        UpdateHighlightBlock();
    }

    private void UpdateHighlightBlock()
    {
        float distance = 0;

        highlightBlockVisible = false;
        highlightBlock.SetActive(false);

        Vector3 lastPosition = Vector3.zero;

        while (distance < maxInteractionDistance)
        {
            Vector3 position = Camera.main.transform.position +
                Camera.main.transform.forward * distance;

            highlightPosition = new Vector3(
                   Mathf.FloorToInt(position.x),
                   Mathf.FloorToInt(position.y),
                   Mathf.FloorToInt(position.z)
                   );

            int blockID = world.GetBlockAtPosition(highlightPosition);
            if (blockID != Chunk.BLOCK_AIR)
            {
                if (Input.GetKeyDown(KeyCode.E))
                    Debug.Log(WorldGenerator.instance.blockTypes[blockID].name);

                highlightBlock.transform.position = highlightPosition + new Vector3(0.5f, 0.5f, 0.5f);

                highlightBlockVisible = true;
                highlightBlock.SetActive(true);

                placeBlockPosition = lastPosition;
                break;
            }

            lastPosition = highlightPosition;
            distance += 0.1f;
        }
    }
}
