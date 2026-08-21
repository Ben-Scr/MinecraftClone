using System.Collections;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    public class DroppedItem : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer itemRenderer;
        [SerializeField] private float liquidRiseSpeed = 1.5f;
        [SerializeField] private float liquidBuoyancyAcceleration = 8f;
        [SerializeField] private float liquidDamping = 2f;
        [SerializeField] private float liquidAngularDamping = 1.2f;
        [SerializeField] private float liquidSurfaceSubmersion = 0.15f;

        private Transform visualTransform;
        private float animationOffset;
        private float pickupAllowedAt;
        private Vector3 attractionStartPosition;
        private Rigidbody itemRigidbody;
        private Collider itemCollider;
        private Coroutine releasePhysicsCoroutine;
        private bool isInLiquid;
        private bool defaultUseGravity;
        private float defaultLinearDamping;
        private float defaultAngularDamping;

        private static readonly Vector3[] liquidCheckDirections =
        {
            Vector3.zero,
            Vector3.up,
            Vector3.down,
            Vector3.left,
            Vector3.right,
            Vector3.forward,
            Vector3.back
        };

        public Rigidbody Rigidbody => itemRigidbody;
        public bool CanBePickedUp => Time.time >= pickupAllowedAt;
        public bool IsAttracting { get; private set; }
        public float AttractionStartedAt { get; private set; }

        private void Awake()
        {
            itemRigidbody = GetComponent<Rigidbody>();
            itemCollider = GetComponent<Collider>();
            defaultUseGravity = itemRigidbody.useGravity;
            defaultLinearDamping = itemRigidbody.linearDamping;
            defaultAngularDamping = itemRigidbody.angularDamping;

            if (itemRenderer == null)
                itemRenderer = GetComponentInChildren<SpriteRenderer>();

            if (itemRenderer == null)
            {
                GameObject visual = new GameObject("Visual");
                visual.transform.SetParent(transform, false);
                itemRenderer = visual.AddComponent<SpriteRenderer>();
            }

            visualTransform = itemRenderer.transform;
            animationOffset = Random.value * Mathf.PI * 2f;
        }

        private void FixedUpdate()
        {
            if (!TerrainGenerator.IsWorldReady)
                return;

            UpdateLiquidPhysics();
        }

        public void Initialize(ItemData itemData, DroppedItemData state, float pickupDelay)
        {
            Initialize(itemData, state, pickupDelay, 0f);
        }

        public void Initialize(ItemData itemData, DroppedItemData state, float pickupDelay, float physicsReleaseDelay)
        {
            ResetRuntimeState();

            gameObject.name = $"Dropped {itemData.Name} x{state.Amount}";
            itemRenderer.sprite = itemData.Sprite;
            visualTransform.localScale = new Vector3(itemData.Size.x, itemData.Size.y, 1f);
            pickupAllowedAt = Time.time + pickupDelay;
            transform.position = state.Position;

            if (physicsReleaseDelay > 0f)
            {
                itemRigidbody.linearVelocity = Vector3.zero;
                itemRigidbody.angularVelocity = Vector3.zero;
                itemRigidbody.isKinematic = true;

                if (itemCollider != null)
                    itemCollider.enabled = false;

                releasePhysicsCoroutine = StartCoroutine(ReleasePhysicsAfterDelay(state.Velocity, physicsReleaseDelay));
            }
            else
            {
                if (itemCollider != null)
                    itemCollider.enabled = true;

                itemRigidbody.isKinematic = false;
                itemRigidbody.linearVelocity = state.Velocity;
            }
        }

        public void ResetForPool()
        {
            ResetRuntimeState();
            gameObject.name = "Dropped Item (Pooled)";
        }

        public void SetAmount(ItemData itemData, int amount)
        {
            gameObject.name = $"Dropped {itemData.Name} x{amount}";
        }

        public void DelayPickup(float delay)
        {
            pickupAllowedAt = Time.time + delay;
        }

        public void BeginAttraction()
        {
            if (IsAttracting)
                return;

            IsAttracting = true;
            AttractionStartedAt = Time.time;
            attractionStartPosition = transform.position;
            itemRigidbody.linearVelocity = Vector3.zero;
            itemRigidbody.angularVelocity = Vector3.zero;
            itemRigidbody.isKinematic = true;
        }

        public void StopAttraction()
        {
            if (!IsAttracting)
                return;

            IsAttracting = false;
            AttractionStartedAt = 0f;
            itemRigidbody.isKinematic = false;
        }

        private void ResetRuntimeState()
        {
            if (releasePhysicsCoroutine != null)
            {
                StopCoroutine(releasePhysicsCoroutine);
                releasePhysicsCoroutine = null;
            }

            IsAttracting = false;
            AttractionStartedAt = 0f;
            pickupAllowedAt = 0f;
            attractionStartPosition = Vector3.zero;
            isInLiquid = false;

            if (itemCollider != null)
                itemCollider.enabled = true;

            if (itemRigidbody == null)
                return;

            itemRigidbody.isKinematic = false;
            itemRigidbody.linearVelocity = Vector3.zero;
            itemRigidbody.angularVelocity = Vector3.zero;
            itemRigidbody.useGravity = defaultUseGravity;
            itemRigidbody.linearDamping = defaultLinearDamping;
            itemRigidbody.angularDamping = defaultAngularDamping;
        }

        private IEnumerator ReleasePhysicsAfterDelay(Vector3 velocity, float delay)
        {
            yield return new WaitForSeconds(delay);

            if (itemCollider != null)
                itemCollider.enabled = true;

            itemRigidbody.isKinematic = false;
            itemRigidbody.linearVelocity = velocity;
            releasePhysicsCoroutine = null;
        }

        private void UpdateLiquidPhysics()
        {
            if (itemRigidbody == null || itemCollider == null)
            {
                return;
            }

            if (itemRigidbody.isKinematic)
            {
                return;
            }

            if (!TryGetLiquidSurfaceY(out float liquidSurfaceY))
            {
                RestoreDefaultPhysics();
                return;
            }

            isInLiquid = true;
            itemRigidbody.useGravity = false;
            itemRigidbody.linearDamping = Mathf.Max(defaultLinearDamping, liquidDamping);
            itemRigidbody.angularDamping = Mathf.Max(defaultAngularDamping, liquidAngularDamping);

            Bounds bounds = itemCollider.bounds;
            float submersion = Mathf.Clamp(liquidSurfaceY - bounds.min.y, 0f, bounds.size.y);
            float targetVerticalSpeed = submersion > liquidSurfaceSubmersion ? liquidRiseSpeed : 0f;

            Vector3 velocity = itemRigidbody.linearVelocity;
            velocity.y = Mathf.MoveTowards(
                velocity.y,
                targetVerticalSpeed,
                liquidBuoyancyAcceleration * Time.fixedDeltaTime);
            itemRigidbody.linearVelocity = velocity;
        }

        private void RestoreDefaultPhysics()
        {
            if (!isInLiquid)
            {
                return;
            }

            isInLiquid = false;
            itemRigidbody.useGravity = defaultUseGravity;
            itemRigidbody.linearDamping = defaultLinearDamping;
            itemRigidbody.angularDamping = defaultAngularDamping;
        }

        private bool TryGetLiquidSurfaceY(out float surfaceY)
        {
            surfaceY = float.NegativeInfinity;

            Bounds bounds = itemCollider.bounds;
            Vector3 center = bounds.center;
            float horizontalExtent = Mathf.Min(bounds.extents.x, bounds.extents.z) * 0.8f;
            float verticalExtent = bounds.extents.y * 0.8f;

            for (int i = 0; i < liquidCheckDirections.Length; i++)
            {
                Vector3 dir = liquidCheckDirections[i];
                Vector3 offset = new Vector3(
                    dir.x * horizontalExtent,
                    dir.y * verticalExtent,
                    dir.z * horizontalExtent);
                Vector3 samplePoint = center + offset;

                if (IsPositionInLiquid(samplePoint))
                {
                    surfaceY = Mathf.Max(surfaceY, Mathf.Floor(samplePoint.y) + 1f);
                }
            }

            return surfaceY > float.NegativeInfinity;
        }

        private static bool IsPositionInLiquid(Vector3 position)
        {
            int blockId = ChunkUtility.GetBlockAtPosition(position);

            if (blockId == Chunk.BLOCK_AIR || AssetsContainer.Instance == null)
            {
                return false;
            }

            BlockData block = AssetsContainer.GetBlock(blockId);
            return block != null && block.IsFluid;
        }

        public void LerpTo(Vector3 target, float duration)
        {
            float progress = duration <= 0f
                ? 1f
                : (Time.time - AttractionStartedAt) / duration;

            Vector3 nextPosition = Vector3.Lerp(
                attractionStartPosition,
                target,
                Mathf.Clamp01(progress));

            if (progress >= 1f)
            {
                itemRigidbody.position = target;
                transform.position = target;
                return;
            }

            itemRigidbody.MovePosition(nextPosition);
        }

        public void UpdateVisual(float time, float bobHeight, float bobSpeed, float spinSpeed)
        {
            float animationTime = time * bobSpeed + animationOffset;
            visualTransform.localPosition = Vector3.up * (Mathf.Sin(animationTime) * bobHeight);

            visualTransform.localEulerAngles += Vector3.up * spinSpeed * Time.deltaTime;
        }
    }
}
