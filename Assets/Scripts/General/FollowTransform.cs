using UnityEngine;

public class FollowTransform : MonoBehaviour
{
    [SerializeField] private bool followX, followY, followZ;
    [SerializeField] private Transform target;

    private void Update()
    {
        Vector3 targetPos = transform.position;

        if (followX)
            targetPos.x = target.position.x;

        if (followY)
            targetPos.y = target.position.y;

        if (followZ)
            targetPos.z = target.position.z;

        transform.position = targetPos;
    }
}
