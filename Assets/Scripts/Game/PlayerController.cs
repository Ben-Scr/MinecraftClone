using UnityEngine;

public class PlayerController : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float walkSpeed = 5f;
    [SerializeField] private float runSpeed = 8f;
    [SerializeField] private float crouchSpeed = 2.5f;
    [SerializeField] private float jumpForce = 5f;

    [Header("Camera")]
    [SerializeField] private float cameraSensitivity = 2f;
    [SerializeField] private float cameraLockMin = -60f;
    [SerializeField] private float cameraLockMax = 60f;
    [SerializeField] private Camera playerCamera;

    private Rigidbody rb;

    [SerializeField] private float doubleSpaceThreshold = 0.2f;
    [SerializeField] private float maxFlySpeedMultiplier = 10f;
    [SerializeField] private float flySpeed = 10f;
    [SerializeField] private float flyAcceleration = 5f;

    private bool isFlying = false;
    private float flySpeedMultiplier = 1;

    private float inputSpace = 0;

    public static PlayerController instance;

    private void Awake()
    {
        instance = this;

        if (playerCamera == null)
            playerCamera = GetComponentInChildren<Camera>();

        rb = GetComponent<Rigidbody>();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    public void Update()
    {
        inputSpace += Time.deltaTime;

        Vector3 input = GetInput();
        rb.linearVelocity = new Vector3(input.x, isFlying ? input.y : rb.linearVelocity.y, input.z);

        Vector3 eulerAnglesY = transform.eulerAngles;
        Vector3 eulerAnglesX = playerCamera.transform.eulerAngles;

        eulerAnglesY.y += Input.GetAxis("Mouse X") * cameraSensitivity;
        eulerAnglesX.x -= Input.GetAxis("Mouse Y") * cameraSensitivity;

        transform.rotation = Quaternion.Euler(eulerAnglesY);
        playerCamera.transform.rotation = Quaternion.Euler
            (
            Mathf.Clamp(eulerAnglesX.x > 180 ? eulerAnglesX.x - 360 : eulerAnglesX.x, cameraLockMin, cameraLockMax),
            transform.eulerAngles.y,
            eulerAnglesX.z
            );

        if (Input.GetKey(KeyCode.Space) && IsGrounded())
        {
            Jump();
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (inputSpace < doubleSpaceThreshold)
            {
                isFlying = !isFlying;

                if (isFlying)
                {
                    rb.linearVelocity = new Vector3(0, 0, 0);
                    rb.useGravity = false;
                }
                else
                {
                    rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0, rb.linearVelocity.z);
                    rb.useGravity = true;
                }
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
    }

    public void Jump()
    {
        rb.linearVelocity = new Vector3(rb.linearVelocity.x, jumpForce, rb.linearVelocity.z);
    }

    public bool IsGrounded()
    {
        return Physics.OverlapCapsule(transform.position, transform.position + new Vector3(0, -0.6f, 0), 0.4f).Length > 1;
    }

    public Vector3 GetInput()
    {
        bool isRunning = Input.GetKey(KeyCode.LeftShift);
        bool isCrouching = Input.GetKey(KeyCode.LeftControl);

        Vector3 input = (Input.GetAxis("Vertical") * transform.forward + Input.GetAxis("Horizontal") * transform.right).normalized;

        float speed = 0;

        if (isFlying)
        {
            if (Input.GetKey(KeyCode.Space))
                input.y +=1;
            if (Input.GetKey(KeyCode.LeftControl))
                input.y -= 1;

            if(Input.GetKey(KeyCode.LeftShift))
            flySpeedMultiplier = Mathf.Lerp(flySpeedMultiplier, maxFlySpeedMultiplier, Time.deltaTime * flyAcceleration);
            else
                flySpeedMultiplier = Mathf.Lerp(flySpeedMultiplier, 1, Time.deltaTime * flyAcceleration);

            speed = flySpeed * flySpeedMultiplier;
        }
        else
        {
            speed = isCrouching ? crouchSpeed : (isRunning ? runSpeed : walkSpeed);
        }

        input *= speed;
        return input;
    }
}
