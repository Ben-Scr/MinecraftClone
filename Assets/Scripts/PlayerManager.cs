using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerManager : MonoBehaviour
{

    const float MAX_DISTANCE = 5;

    public WorldGenerator world;

    public GameObject highlightBlock;

    Vector3 highlightPosition;
    Vector3 placeBlockPosition;

    bool highlightBlockVisible = false;

    CharacterController characterController;

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
    }

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

                Vector3 feetPosition = new Vector3(transform.position.x, characterController.bounds.min.y, transform.position.z);
                Vector3 headPosition = new Vector3(transform.position.x, characterController.bounds.max.y, transform.position.z);

                float distanceToFeet = Vector3.Distance(feetPosition, placeBlockPosition + new Vector3(0.5f, 0.5f, 0.5f));
                float distanceToHead = Vector3.Distance(headPosition, placeBlockPosition + new Vector3(0.5f, 0.5f, 0.5f));

                if (!isSamePositionAsPlayer || (distanceToFeet > 1.0f && distanceToHead > 1.0f))
                {
                    world.SetBlock(placeBlockPosition, Chunk.BLOCK_LEAVES);
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

        while(distance < MAX_DISTANCE)
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
