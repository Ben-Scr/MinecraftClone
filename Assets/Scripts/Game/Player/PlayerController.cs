using System;
using UnityEngine;
using UnityEngine.Serialization;
using static BenScr.MinecraftClone.SettingsContainer;

namespace BenScr.MinecraftClone
{
    public enum GameMode
    {
        Survival = 0,
        Exploration = 1,
        Creative = 2,
        Spectator = 3,
    }
    public enum MovementMode
    {
        Default,
        Flying
    }

    public class PlayerController : MonoBehaviour
    {
        [Header("Movement")]
        public GameMode GameMode = GameMode.Survival;

        [SerializeField] private float walkSpeed = 5f;
        [SerializeField] private float runSpeed = 8f;
        [SerializeField] private float crouchSpeed = 2.5f;
        [SerializeField] private float jumpForce = 5f;
        private bool isGrounded;

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

        private float curFlySpeedMultiplier = 1;
        [FormerlySerializedAs("isFlying")]
        public bool IsFlying;


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
        [SerializeField] private float swimSurfaceSinkSpeed = 0.75f;
        [SerializeField] private float swimSurfacePushSpeed = 6f;
        [SerializeField] private float swimSurfaceJumpForceMultiplier = 0.75f;
        [SerializeField] private float swimSurfacePushMinSubmersion = 0.9f;
        [SerializeField] private float swimSurfacePushCooldown = 0.35f;

        [SerializeField] private float gravity;
        private UnderwaterPostEffect underwaterEffect;
        internal bool isHeadInFluid;
        private bool isInFluid;
        internal BlockData currentFluidBlock;
        private bool hasFluidSurface;
        private float currentFluidSurfaceY;
        private float nextSurfaceSwimPushTime;
        private float defaultDrag;
        private float defaultAngularDrag;

        private Rigidbody rb;
        private CapsuleCollider capsuleCollider;
        private int groundCollisionMask;

        private float inputSpace = 0;
        public static PlayerController Instance { get; private set; }

        public static Action<GameMode> OnSwitchGameMode;

        internal Quaternion SavedBodyRotation => playerMeshTr != null
            ? playerMeshTr.rotation
            : transform.rotation;

        internal Quaternion SavedCameraRotation => playerCamera != null
            ? playerCamera.transform.rotation
            : SavedBodyRotation;

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

        private void Awake()
        {
            Instance = this;

            if (playerCamera == null)
                playerCamera = GetComponentInChildren<Camera>();

            rb = GetComponent<Rigidbody>();
            capsuleCollider = GetComponentInChildren<CapsuleCollider>();
            groundCollisionMask = ~LayerMask.GetMask("Player");
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
                rb.useGravity = false;
            }

            ApplyGameModeState(resetVelocity: false);
        }

        private void OnEnable()
        {
            GameController.OnFreeze += OnPlayerFreeze;
            GameController.OnUnFreeze += OnPlayerUnFreeze;

            if (GameController.IsPlayerFrozen)
                OnPlayerFreeze(FreezeReason.LoadingTerrain);
        }
        private void OnDisable()
        {
            GameController.OnFreeze -= OnPlayerFreeze;
            GameController.OnUnFreeze -= OnPlayerUnFreeze;
        }

        private void OnPlayerFreeze(FreezeReason freezeReason)
        {
            if (GameController.IsPlayerFrozen)
                rb.constraints = RigidbodyConstraints.FreezeAll;

        }
        private void OnPlayerUnFreeze(FreezeReason freezeReason)
        {

            if (!GameController.IsPlayerFrozen)
                ApplyGameModeState(resetVelocity: false);
        }

        public void Update()
        {
            if (GameController.IsPlayerFrozen) return;

            isGrounded = IsGrounded();
            inputSpace += Time.deltaTime;

            UpdateFluidState();
            Movement();
            Rotation();
        }

        private void Rotation()
        {
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
        }

        internal void RestoreSavedTransform(
            Vector3 position,
            Quaternion bodyRotation,
            Quaternion cameraRotation)
        {
            transform.position = position;

            if (playerMeshTr != null)
                playerMeshTr.rotation = bodyRotation;
            else
                transform.rotation = bodyRotation;

            if (playerCamera != null)
                playerCamera.transform.rotation = cameraRotation;

            if (rb != null)
            {
                rb.position = position;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        private void Movement()
        {
            Vector3 input = GetInput();


            if (GameMode != GameMode.Spectator)
            {
                if (isInFluid && !IsFlying)
                {
                    Vector3 currentVelocity = rb.linearVelocity;
                    Vector3 targetVelocity = new Vector3(input.x, input.y, input.z);

                    bool isTryingToSwimUp = Input.GetKey(KeyCode.Space);
                    if (!isHeadInFluid)
                    {
                        if (isTryingToSwimUp && TryApplySurfaceSwimPush(ref currentVelocity))
                        {
                            targetVelocity.y = -Mathf.Abs(swimSurfaceSinkSpeed);
                        }
                        else if (targetVelocity.y > 0f)
                        {
                            targetVelocity.y = -Mathf.Abs(swimSurfaceSinkSpeed);
                        }
                    }

                    rb.linearVelocity = BlendFluidVelocity(currentVelocity, targetVelocity);
                }
                else
                {
                    rb.linearVelocity = GetMovementVelocity(input);
                }
            }
            else
            {
                transform.position += input * Time.deltaTime;
            }

            if (Input.GetKey(KeyCode.Space) && !isInFluid && isGrounded && rb.linearVelocity.y <= 0.1f)
            {
                Jump();
            }

            if (Input.GetKeyDown(KeyCode.Space) && GameMode == GameMode.Creative)
            {
                if (GameMode != GameMode.Spectator && inputSpace < doubleSpaceThreshold)
                {
                    SetFlyingMode();
                }
                else
                {
                    inputSpace = 0;
                }
            }

            if (Input.GetKeyDown(KeyCode.F1))
            {
                SetGameMode(GameMode == GameMode.Spectator ? GameMode.Survival : GameMode.Spectator);
            }
            if (Input.GetKeyDown(KeyCode.F2))
            {
                SetGameMode(GameMode.Creative);
            }

            if (!IsFlying && !isInFluid && isGrounded && rb.linearVelocity.magnitude < 0.1f)
            {
                rb.linearVelocity = Vector3.zero;
            }
        }

        private Vector3 GetMovementVelocity(Vector3 input)
        {
            if (IsFlying)
            {
                input.y = Mathf.Clamp(input.y, minVelocityY, maxVelocityY);
                return input;
            }

            Vector3 velocity = rb.linearVelocity;
            velocity.x = input.x;
            velocity.z = input.z;

            if (!isGrounded)
            {
                velocity.y -= gravity * Time.deltaTime;
            }
            else if (velocity.y < 0f)
            {
                velocity.y = 0f;
            }

            velocity.y = Mathf.Clamp(velocity.y, minVelocityY, maxVelocityY);
            return velocity;
        }

        public void SetFlyingMode()
        {
            if (GameMode != GameMode.Creative)
                return;

            IsFlying = !IsFlying;

            if (IsFlying)
            {
                rb.linearVelocity = new Vector3(0, 0, 0);
                curFlySpeedMultiplier = 1;
                rb.useGravity = false;
            }
            else
            {
                rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0, rb.linearVelocity.z);
                rb.useGravity = false;
            }
        }

        public void SetGameMode(GameMode targetGameMode)
        {
            if (GameMode == targetGameMode)
            {
                ApplyGameModeState(resetVelocity: false);
                OnSwitchGameMode?.Invoke(GameMode);
                return;
            }

            GameMode = targetGameMode;
            ApplyGameModeState(resetVelocity: true);
            OnSwitchGameMode?.Invoke(GameMode);
        }

        public void SetSpectatorMode()
        {
            SetGameMode(GameMode == GameMode.Spectator ? GameMode.Survival : GameMode.Spectator);
        }

        private void ApplyGameModeState(bool resetVelocity)
        {
            bool isSpectator = GameMode == GameMode.Spectator;

            if (isSpectator)
                IsFlying = true;
            else if (GameMode != GameMode.Creative)
                IsFlying = false;

            if (capsuleCollider != null)
                capsuleCollider.enabled = !isSpectator;

            if (rb == null)
                return;

            rb.constraints = isSpectator ? RigidbodyConstraints.FreezeAll : RigidbodyConstraints.FreezeRotation;
            rb.useGravity = false;

            if (resetVelocity)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                curFlySpeedMultiplier = 1f;
            }
        }

        public void Jump()
        {
            if (isInFluid && !IsFlying)
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
            return Physics.CheckBox(
                transform.position + groundedOffset,
                groundedSize / 2f,
                Quaternion.identity,
                groundCollisionMask);
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

            if (isInFluid && !IsFlying)
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
                    vertical = isHeadInFluid ? swimBuoyancy : -Mathf.Abs(swimSurfaceSinkSpeed);
                }


                velocity.y = Mathf.Clamp(vertical, -swimVerticalSpeed, swimVerticalSpeed);
                return velocity;
            }

            float speed = 0f;

            if (IsFlying)
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


            bool wasInFluid = isInFluid;
            isInFluid = TryGetFluidBlock(out currentFluidBlock);
            isHeadInFluid = CheckHeadInFluid();
            hasFluidSurface = isInFluid && TryGetFluidSurfaceY(out currentFluidSurfaceY);

            if (IsFlying)
            {
                return;
            }

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
            rb.useGravity = false;
            rb.linearDamping = defaultDrag;
            rb.angularDamping = defaultAngularDrag;
            currentFluidBlock = null;
            isInFluid = false;
            isHeadInFluid = false;
            hasFluidSurface = false;
        }

        private Vector3 BlendFluidVelocity(Vector3 currentVelocity, Vector3 targetVelocity)
        {
            float blend = Mathf.Clamp01(Time.deltaTime * swimLerpSpeed);
            Vector3 blendedVelocity = Vector3.Lerp(currentVelocity, targetVelocity, blend);

            float minVerticalSpeed = currentVelocity.y < -swimVerticalSpeed
                ? currentVelocity.y
                : -swimVerticalSpeed;
            float surfacePushSpeed = GetSurfaceSwimPushSpeed();
            float maxVerticalSpeed = Mathf.Max(swimVerticalSpeed, surfacePushSpeed);

            blendedVelocity.y = Mathf.Clamp(blendedVelocity.y, minVerticalSpeed, maxVerticalSpeed);
            return blendedVelocity;
        }

        private bool TryApplySurfaceSwimPush(ref Vector3 currentVelocity)
        {
            if (Time.time < nextSurfaceSwimPushTime)
            {
                return false;
            }

            if (currentVelocity.y < -swimVerticalSpeed)
            {
                return false;
            }

            if (GetFluidSubmersionDepth() < swimSurfacePushMinSubmersion)
            {
                return false;
            }

            currentVelocity.y = Mathf.Max(currentVelocity.y, GetSurfaceSwimPushSpeed());
            nextSurfaceSwimPushTime = Time.time + swimSurfacePushCooldown;
            return true;
        }

        private float GetSurfaceSwimPushSpeed()
        {
            return Mathf.Max(swimSurfacePushSpeed, jumpForce * swimSurfaceJumpForceMultiplier);
        }

        private float GetFluidSubmersionDepth()
        {
            if (!hasFluidSurface || capsuleCollider == null)
            {
                return 0f;
            }

            Bounds bounds = capsuleCollider.bounds;
            return Mathf.Clamp(currentFluidSurfaceY - bounds.min.y, 0f, bounds.size.y);
        }

        private bool TryGetFluidSurfaceY(out float surfaceY)
        {
            surfaceY = float.NegativeInfinity;

            if (capsuleCollider == null)
            {
                return false;
            }

            Bounds bounds = capsuleCollider.bounds;
            Vector3 center = bounds.center;
            float horizontalExtent = Mathf.Min(bounds.extents.x, bounds.extents.z) * 0.8f;
            int minY = Mathf.FloorToInt(bounds.min.y - 0.05f);
            int maxY = Mathf.FloorToInt(bounds.max.y + 0.05f);

            for (int i = 0; i < fluidCheckDirections.Length; i++)
            {
                Vector3 dir = fluidCheckDirections[i];

                if (dir.y != 0f)
                {
                    continue;
                }

                float sampleX = center.x + dir.x * horizontalExtent;
                float sampleZ = center.z + dir.z * horizontalExtent;

                for (int y = maxY; y >= minY; y--)
                {
                    Vector3 samplePoint = new Vector3(sampleX, y + 0.5f, sampleZ);

                    if (IsPositionInFluid(samplePoint))
                    {
                        surfaceY = Mathf.Max(surfaceY, y + 1f);
                        break;
                    }
                }
            }

            return surfaceY > float.NegativeInfinity;
        }

        private bool CheckHeadInFluid()
        {
            if (playerCamera != null)
            {
                return IsPositionInFluid(playerCamera.transform.position);
            }

            if (capsuleCollider != null)
            {
                Bounds bounds = capsuleCollider.bounds;
                Vector3 headPosition = bounds.center + Vector3.up * bounds.extents.y;
                return IsPositionInFluid(headPosition);
            }

            return false;
        }

        private static bool IsPositionInFluid(Vector3 position)
        {
            int blockId = ChunkUtility.GetBlockAtPosition(position);

            if (blockId == Chunk.BLOCK_AIR)
            {
                return false;
            }

            if (AssetsContainer.Instance == null)
            {
                return false;
            }

            BlockData block = AssetsContainer.GetBlock(blockId);
            return block != null && block.IsFluid;
        }


        private bool TryGetFluidBlock(out BlockData fluidBlock)
        {
            fluidBlock = null;

            if (AssetsContainer.Instance == null)
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

                BlockData block = AssetsContainer.GetBlock(blockId);

                if (block != null && block.IsFluid)
                {
                    fluidBlock = block;
                    return true;
                }
            }

            return false;
        }

        private void OnDrawGizmos()
        {
            if (!Settings?.DebugGizmos ?? false) return;

            Gizmos.DrawWireCube(transform.position + groundedOffset, groundedSize / 2f);
        }
    }
}
