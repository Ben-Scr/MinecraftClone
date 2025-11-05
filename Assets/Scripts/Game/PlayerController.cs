using UnityEngine;

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

        private float curFlySpeedMultiplier = 1;
        private bool isFlying => movementMode == MovementMode.Flying;
        private bool isSpectator => isFlying && !boxCollider.enabled;

        private Rigidbody rb;
        private BoxCollider boxCollider;

        private float inputSpace = 0;
        public static PlayerController instance;

        private void Awake()
        {
            instance = this;

            if (playerCamera == null)
                playerCamera = GetComponentInChildren<Camera>();

            rb = GetComponent<Rigidbody>();
            boxCollider = GetComponentInChildren<BoxCollider>();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        public void Update()
        {
            inputSpace += Time.deltaTime;

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

            if (Input.GetKey(KeyCode.Space) && IsGrounded())
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
            float velocityY = Mathf.Clamp(isFlying ? input.y : rb.linearVelocity.y, minVelocityY, maxVelocityY);
            input.y = velocityY;

            if (!isSpectator)
                rb.linearVelocity = input;
            else
                transform.position += input * Time.deltaTime;
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
                rb.useGravity = true;
            }
        }

        public void SetSpectatorMode()
        {
            if (!isFlying || isSpectator) SetFlyingMode();
            boxCollider.enabled = !isFlying;
            rb.constraints = isFlying ? RigidbodyConstraints.FreezeAll : RigidbodyConstraints.FreezeRotation;
        }

        public void Jump()
        {
            rb.linearVelocity = new Vector3(rb.linearVelocity.x, jumpForce, rb.linearVelocity.z);
        }

        public bool IsGrounded()
        {
            return Physics.CheckBox(transform.position + new Vector3(0, -0.1f, 0), transform.localScale / 2.1f, Quaternion.identity, ~LayerMask.GetMask("Player"));
        }

        public Vector3 GetInput()
        {
            bool isRunning = Input.GetKey(KeyCode.LeftShift);
            bool isCrouching = Input.GetKey(KeyCode.LeftControl);

            Vector3 input = (Input.GetAxis("Vertical") * playerMeshTr.forward + Input.GetAxis("Horizontal") * playerMeshTr.right).normalized;

            float speed = 0;

            if (isFlying)
            {
                if (Input.GetKey(KeyCode.Space))
                    input.y += 1;
                if (Input.GetKey(KeyCode.LeftControl))
                    input.y -= 1;

                if (Input.GetKey(KeyCode.LeftShift))
                    curFlySpeedMultiplier = Mathf.Lerp(curFlySpeedMultiplier, maxFlySpeedMultiplier, Time.deltaTime * flyAcceleration);
                else if (input == Vector3.zero)
                    curFlySpeedMultiplier = Mathf.Lerp(curFlySpeedMultiplier, 1, Time.deltaTime * flyAcceleration);

                speed = flySpeed * curFlySpeedMultiplier;
            }
            else
            {
                speed = isCrouching ? crouchSpeed : (isRunning ? runSpeed : walkSpeed);
            }

            input *= speed;
            return input;
        }
    }
}