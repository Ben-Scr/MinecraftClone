using UnityEngine;

public class PlayerManager : MonoBehaviour
{
    private static readonly Vector3 HalfExtents = new Vector3(0.499f, 0.499f, 0.499f);

    [SerializeField] private float maxInteractionDistance = 5;

    public TerrainGenerator world;

    public GameObject highlightBlock;

    Vector3 highlightPosition;
    Vector3 placeBlockPosition;

    bool highlightBlockVisible = false;

    [SerializeField] private float breakBlockCooldown = 0.1f;
    [SerializeField] private float placeBlockCooldown = 0.1f;

    private float breakBlockTimer = 0f;
    private float placeBlockTimer = 0f;

    public Block selectedBlock;

    void Update()
    {
        breakBlockTimer += Time.deltaTime;
        placeBlockTimer += Time.deltaTime;

        if (highlightBlockVisible)
        {
            if (Input.GetMouseButton(0) && breakBlockTimer > breakBlockCooldown)
            {
                breakBlockTimer = 0f;
                world.SetBlock(highlightPosition, Chunk.BLOCK_AIR);
            }

            if (Input.GetMouseButton(1) && placeBlockTimer > placeBlockCooldown)
            {
                placeBlockTimer = 0f;
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
                    Debug.Log(AssetsContainer.GetBlock(blockID).name);

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
