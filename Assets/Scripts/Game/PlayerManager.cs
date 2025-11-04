using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerManager : MonoBehaviour
{

    [SerializeField] private float maxInteractionDistance = 5;
    [SerializeField] private float minDistanceToHead = 0.8f;
    [SerializeField] private float minDistanceToFeet = 0.8f;

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

            if(Input.GetMouseButtonDown(1))
            {
                bool isSamePositionAsPlayer = placeBlockPosition.x == Mathf.FloorToInt(transform.position.x) &&
                    placeBlockPosition.z == Mathf.FloorToInt(transform.position.z);


                bool overlapsWithPlayer = false;

                if (!isSamePositionAsPlayer || !overlapsWithPlayer)
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

        while(distance < maxInteractionDistance)
        {
            Vector3 position = Camera.main.transform.position +
                Camera.main.transform.forward * distance;

            highlightPosition = new Vector3(
                   Mathf.FloorToInt(position.x),
                   Mathf.FloorToInt(position.y),
                   Mathf.FloorToInt(position.z)
                   );

            if (world.GetBlockAtPosition(position) != Chunk.BLOCK_AIR)
            {
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
