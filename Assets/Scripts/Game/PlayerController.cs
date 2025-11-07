using UnityEngine;
using static BenScr.MCC.SettingsContainer;

namespace BenScr.MCC
{
    public enum MovementMode
    {
        Default,
        Flying
    }

    public class PlayerController : MonoBehaviour
    {
        [Header("Movement")]
        public MovementMode movementMode = MovementMode.Default;
        [SerializeField] private float walkSpeed = 5f;
        [SerializeField] private float runSpeed = 8f;
        [SerializeField] private float crouchSpeed = 2.5f;
        [SerializeField] private float jumpForce = 5f;

        [Header("Camera")]
        [SerializeField] private float cameraSensitivity = 2f;
        [SerializeField] private float cameraLockMin = -60f;
        [SerializeField] private float cameraLockMax = 60f;
        [SerializeField] private Camera playerCamera;
        [SerializeField] private Transform playerMeshTr;

        [Header("Flying Mode")]
        [SerializeField] private float doubleSpaceThreshold = 0.2f;
        [SerializeField] private float maxFlySpeedMultiplier = 10f;
        [SerializeField] private float flySpeed = 10f;
        [SerializeField] private float flyAcceleration = 5f;

        [Header("Physics")]
        [SerializeField] private float maxVelocityY = 50f;
        [SerializeField] private float minVelocityY = -50f;
        [SerializeField] private Vector3 groundedSize;
        [SerializeField] private Vector3 groundedOffset;

        [Header("Fluid Movement")]
        [SerializeField] private float swimSpeed = 2.75f;
        [SerializeField] private float swimVerticalSpeed = 2f;
        [SerializeField] private float swimBuoyancy = 0.6f;
        [SerializeField] private float swimLerpSpeed = 5f;
        [SerializeField] private float swimDrag = 3f;
        [SerializeField] private float swimAngularDrag = 1.5f;
        private UnderwaterPostEffect underwaterEffect;

        private float curFlySpeedMultiplier = 1;
        private bool isFlying => movementMode == MovementMode.Flying;
        private bool isSpectator => isFlying && !capsuleCollider.enabled;

        private Rigidbody rb;
        private CapsuleCollider capsuleCollider;
        private bool isInFluid;
        private Block currentFluidBlock;
        private float defaultDrag;
        private float defaultAngularDrag;

        private float inputSpace = 0;
        public static PlayerController instance;

        public bool IsInFluid => isInFluid;
        public Block CurrentFluidBlock => currentFluidBlock;

        private void Awake()
        {
            instance = this;

            if (playerCamera == null)
                playerCamera = GetComponentInChildren<Camera>();

            rb = GetComponent<Rigidbody>();
            capsuleCollider = GetComponentInChildren<CapsuleCollider>();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            if (playerCamera != null)
            {
                playerCamera.depthTextureMode |= DepthTextureMode.Depth;

                if (!playerCamera.TryGetComponent(out underwaterEffect))
                {
                    underwaterEffect = playerCamera.gameObject.AddComponent<UnderwaterPostEffect>();
                }

                underwaterEffect.Initialize(this);
            }

            if (rb != null)
            {
                defaultDrag = rb.linearDamping;
                defaultAngularDrag = rb.angularDamping;
            }
        }

        public void Update()
        {
            inputSpace += Time.deltaTime;


            UpdateFluidState();
            Movement();

            Vector3 eulerAnglesY = playerMeshTr.eulerAngles;
            Vector3 eulerAnglesX = playerCamera.transform.eulerAngles;

            eulerAnglesY.y += Input.GetAxis("Mouse X") * cameraSensitivity;
            eulerAnglesX.x -= Input.GetAxis("Mouse Y") * cameraSensitivity;

            playerMeshTr.rotation = Quaternion.Euler(eulerAnglesY);
            playerCamera.transform.rotation = Quaternion.Euler
                (
                Mathf.Clamp(eulerAnglesX.x > 180 ? eulerAnglesX.x - 360 : eulerAnglesX.x, cameraLockMin, cameraLockMax),
                playerMeshTr.eulerAngles.y,
                eulerAnglesX.z
                );


            if (!isFlying && !isInFluid && IsGrounded() && rb.linearVelocity.magnitude < 0.1f)
            {
                rb.linearVelocity = Vector3.zero;
            }

            if (Input.GetKey(KeyCode.Space) && !isInFluid && IsGrounded())
            {
                Jump();
            }

            if (Input.GetKeyDown(KeyCode.Space))
            {
                if (!isSpectator && inputSpace < doubleSpaceThreshold)
                {
                    SetFlyingMode();
                }
                else
                {
                    inputSpace = 0;
                }
            }


            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (Input.anyKeyDown)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            if (Input.GetKeyDown(KeyCode.F1))
            {
                SetSpectatorMode();
            }
        }

        private void Movement()
        {
            Vector3 input = GetInput();

            if (!isSpectator)
            {
                if (isInFluid && !isFlying)
                {
                    Vector3 currentVelocity = rb.linearVelocity;
                    Vector3 targetVelocity = new Vector3(input.x, input.y, input.z);
                    Vector3 blendedVelocity = Vector3.Lerp(currentVelocity, targetVelocity, Time.deltaTime * swimLerpSpeed);
                    blendedVelocity.y = Mathf.Clamp(blendedVelocity.y, -swimVerticalSpeed, swimVerticalSpeed);
                    rb.linearVelocity = blendedVelocity;
                }
                else
                {
                    float velocityY = Mathf.Clamp(isFlying ? input.y : rb.linearVelocity.y, minVelocityY, maxVelocityY);
                    input.y = velocityY;
                    rb.linearVelocity = input;
                }
            }
            else
            {
                transform.position += input * Time.deltaTime;
            }
        }

        public void SetFlyingMode()
        {
            movementMode = movementMode == MovementMode.Default ? MovementMode.Flying : MovementMode.Default;

            if (isFlying)
            {
                rb.linearVelocity = new Vector3(0, 0, 0);
                curFlySpeedMultiplier = 1;
                rb.useGravity = false;
            }
            else
            {
                rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0, rb.linearVelocity.z);
                rb.useGravity = !isInFluid;
            }
        }

        public void SetSpectatorMode()
        {
            if (!isFlying || isSpectator) SetFlyingMode();
            capsuleCollider.enabled = !isFlying;
            rb.constraints = isFlying ? RigidbodyConstraints.FreezeAll : RigidbodyConstraints.FreezeRotation;
        }

        public void Jump()
        {
            if (isInFluid && !isFlying)
            {
                rb.linearVelocity = new Vector3(rb.linearVelocity.x, swimVerticalSpeed, rb.linearVelocity.z);
            }
            else
            {
                rb.linearVelocity = new Vector3(rb.linearVelocity.x, jumpForce, rb.linearVelocity.z);
            }
        }

        public bool IsGrounded()
        {
            return Physics.CheckBox(transform.position + groundedOffset, groundedSize / 2f, Quaternion.identity, ~LayerMask.GetMask("Player"));
        }

        public Vector3 GetInput()
        {
            bool isRunning = Input.GetKey(KeyCode.LeftShift);
            bool isCrouching = Input.GetKey(KeyCode.LeftControl);

            Vector3 moveInput = Input.GetAxis("Vertical") * playerMeshTr.forward + Input.GetAxis("Horizontal") * playerMeshTr.right;
            if (moveInput.sqrMagnitude > 1f)
            {
                moveInput.Normalize();
            }

            if (isInFluid && !isFlying)
            {
                Vector3 velocity = moveInput * swimSpeed;

                bool ascend = Input.GetKey(KeyCode.Space);
                bool descend = Input.GetKey(KeyCode.LeftControl);

                float vertical = 0f;
                if (ascend)
                {
                    vertical += swimVerticalSpeed;
                }
                if (descend)
                {
                    vertical -= swimVerticalSpeed;
                }
                if (!ascend && !descend)
                {
                    vertical = swimBuoyancy;
                }

                velocity.y = Mathf.Clamp(vertical, -swimVerticalSpeed, swimVerticalSpeed);
                return velocity;
            }

            float speed = 0f;

            if (isFlying)
            {
                if (Input.GetKey(KeyCode.Space))
                    moveInput.y += 1;
                if (Input.GetKey(KeyCode.LeftControl))
                    moveInput.y -= 1;

                if (Input.GetKey(KeyCode.LeftShift))
                    curFlySpeedMultiplier = Mathf.Lerp(curFlySpeedMultiplier, maxFlySpeedMultiplier, Time.deltaTime * flyAcceleration);
                else if (moveInput == Vector3.zero)
                    curFlySpeedMultiplier = Mathf.Lerp(curFlySpeedMultiplier, 1, Time.deltaTime * flyAcceleration);

                speed = flySpeed * curFlySpeedMultiplier;
                moveInput *= speed;
                return moveInput;
            }

            speed = isCrouching ? crouchSpeed : (isRunning ? runSpeed : walkSpeed);
            moveInput *= speed;
            return moveInput;
        }

        private void UpdateFluidState()
        {
            if (rb == null || capsuleCollider == null)
                return;

            if (isFlying)
            {
                if (isInFluid)
                {
                    ExitFluid();
                }
                return;
            }

            bool wasInFluid = isInFluid;
            isInFluid = TryGetFluidBlock(out currentFluidBlock);

            if (isInFluid)
            {
                rb.useGravity = false;
                rb.linearDamping = swimDrag;
                rb.angularDamping = swimAngularDrag;
            }
            else if (wasInFluid)
            {
                ExitFluid();
            }
        }

        private void ExitFluid()
        {
            rb.useGravity = !isFlying;
            rb.linearDamping = defaultDrag;
            rb.angularDamping = defaultAngularDrag;
            currentFluidBlock = null;
            isInFluid = false;
        }

        private bool TryGetFluidBlock(out Block fluidBlock)
        {
            fluidBlock = null;

            if (AssetsContainer.instance == null)
            {
                return false;
            }

            if (capsuleCollider == null)
            {
                return false;
            }

            Bounds bounds = capsuleCollider.bounds;
            Vector3 center = bounds.center;
            float horizontalExtent = Mathf.Min(bounds.extents.x, bounds.extents.z) * 0.9f;
            float verticalExtent = bounds.extents.y * 0.9f;

            for (int i = 0; i < fluidCheckDirections.Length; i++)
            {
                Vector3 dir = fluidCheckDirections[i];
                Vector3 offset = new Vector3(dir.x * horizontalExtent, dir.y * verticalExtent, dir.z * horizontalExtent);
                Vector3 samplePoint = center + offset;

                int blockId = ChunkUtility.GetBlockAtPosition(samplePoint);

                if (blockId == Chunk.BLOCK_AIR)
                {
                    continue;
                }

                Block block = AssetsContainer.GetBlock(blockId);

                if (block != null && block.isFluid)
                {
                    fluidBlock = block;
                    return true;
                }
            }

            return false;
        }

        private static readonly Vector3[] fluidCheckDirections = new Vector3[]
        {
            Vector3.zero,
            Vector3.up,
            Vector3.down,
            Vector3.left,
            Vector3.right,
            Vector3.forward,
            Vector3.back
        };

        private void OnDrawGizmos()
        {
            if(!Settings?.DebugGizmos ?? false) return;

            Gizmos.DrawWireCube(transform.position + groundedOffset, groundedSize / 2f);
        }
    }
}