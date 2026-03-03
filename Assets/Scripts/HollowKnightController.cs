using UnityEngine;

/// <summary>
/// Hollow Knight–inspired 2D character controller for Unity.
/// Focus: "feel" (forgiveness, gravity shaping, crisp dash, wall slide/jump).
/// 
/// Assumes:
/// - Rigidbody2D + Collider2D on same GameObject
/// - GroundCheck / WallCheckL / WallCheckR Transforms assigned
/// - Platforms/walls are in groundMask
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class HollowKnightController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform groundCheck;
    [SerializeField] private Transform wallCheckL;
    [SerializeField] private Transform wallCheckR;
    [SerializeField] private LayerMask groundMask;

    [Header("Collision Checks")]
    [SerializeField] private float groundCheckRadius = 0.12f;
    [SerializeField] private float wallCheckRadius = 0.12f;

    [Header("Run Feel")]
    [SerializeField] private float maxRunSpeed = 7.5f;
    [SerializeField] private float groundAccel = 80f;
    [SerializeField] private float groundDecel = 95f;
    [SerializeField] private float airAccel = 55f;
    [SerializeField] private float airDecel = 45f;
    [SerializeField] private float turnBoost = 1.15f; // extra snap when reversing direction

    [Header("Jump Feel")]
    [SerializeField] private float jumpVelocity = 14.0f;
    [SerializeField] private float coyoteTime = 0.10f;
    [SerializeField] private float jumpBufferTime = 0.10f;
    [SerializeField] private float jumpCutMultiplier = 0.55f; // release jump early -> cut upward vel
    [SerializeField] private float apexHangTime = 0.06f;      // tiny hang near top like HK
    [SerializeField] private float apexHangThreshold = 1.0f;  // when |vy| is small, hang
    [SerializeField] private float minJumpVelocityForCut = 2.0f;

    [Header("Gravity Shaping")]
    [SerializeField] private float baseGravityScale = 3.8f;
    [SerializeField] private float fallGravityMultiplier = 1.35f;  // heavier on descent
    [SerializeField] private float fastFallGravityMultiplier = 1.65f;
    [SerializeField] private float maxFallSpeed = 18.5f;
    [SerializeField] private float maxFastFallSpeed = 22.0f;
    [SerializeField] private bool allowFastFall = true;

    [Header("Dash Feel")]
    [SerializeField] private float dashSpeed = 18.5f;
    [SerializeField] private float dashDuration = 0.16f;
    [SerializeField] private float dashCooldown = 0.28f;
    [SerializeField] private float dashBufferTime = 0.10f;
    [SerializeField] private bool dashStopsVertical = true;

    [Header("Wall Feel")]
    [SerializeField] private float wallSlideSpeed = 4.0f;
    [SerializeField] private float wallSlideAccel = 60f;
    [SerializeField] private float wallJumpVelocityX = 9.0f;
    [SerializeField] private float wallJumpVelocityY = 14.0f;
    [SerializeField] private float wallJumpLockTime = 0.10f; // brief lock so you don't instantly re-stick
    [SerializeField] private float wallStickTime = 0.08f;    // tiny "stick" before sliding

    [Header("Input")]
    [SerializeField] private KeyCode jumpKey = KeyCode.Space;
    [SerializeField] private KeyCode dashKey = KeyCode.LeftShift;
    [SerializeField] private KeyCode downKey = KeyCode.S; // for fast-fall (optional)

    // Components
    private Rigidbody2D rb;

    // State
    private enum MoveState { Normal, Dashing, WallSticking }
    private MoveState state = MoveState.Normal;

    // Facing
    private int facing = 1; // 1 right, -1 left

    // Collision flags
    private bool isGrounded;
    private bool onWallL;
    private bool onWallR;
    private bool onWall => onWallL || onWallR;
    private int wallDir => onWallL ? -1 : (onWallR ? 1 : 0);

    // Timers
    private float coyoteTimer;
    private float jumpBufferTimer;
    private float dashBufferTimer;
    private float dashTimer;
    private float dashCooldownTimer;
    private float wallJumpLockTimer;
    private float wallStickTimer;
    private float apexHangTimer;

    // Inputs
    private float moveX;
    private bool jumpPressed;
    private bool jumpHeld;
    private bool dashPressed;
    private bool downHeld;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        rb.gravityScale = baseGravityScale;
        rb.freezeRotation = true;
    }

    private void Update()
    {
        ReadInputs();
        UpdateChecks();
        UpdateTimers(Time.deltaTime);

        // Buffering
        if (jumpPressed) jumpBufferTimer = jumpBufferTime;
        if (dashPressed) dashBufferTimer = dashBufferTime;

        // Facing
        if (Mathf.Abs(moveX) > 0.01f && state != MoveState.Dashing && wallJumpLockTimer <= 0f)
            facing = moveX > 0 ? 1 : -1;
    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        switch (state)
        {
            case MoveState.Dashing:
                FixedDash(dt);
                break;

            case MoveState.WallSticking:
                FixedWallStick(dt);
                break;

            default:
                FixedNormal(dt);
                break;
        }

        ClampFallSpeed();
    }

    // =========================
    // Core Fixed State Behaviors
    // =========================

    private void FixedNormal(float dt)
    {
        // Dash attempt (buffered)
        if (dashBufferTimer > 0f && dashCooldownTimer <= 0f)
        {
            StartDash();
            return;
        }

        // Wall stick (tiny delay before sliding feels HK-like)
        if (!isGrounded && onWall && wallJumpLockTimer <= 0f && Mathf.Abs(moveX) > 0.01f && moveX * wallDir > 0f)
        {
            // Player is pushing into the wall in the direction of the wall
            state = MoveState.WallSticking;
            wallStickTimer = wallStickTime;
            // Cancel some downward speed so you "catch" the wall
            if (rb.linearVelocity.y < 0f)
                rb.linearVelocity = new Vector2(rb.linearVelocity.x, Mathf.Max(rb.linearVelocity.y, -2.0f));
        }

        // Horizontal movement
        ApplyRun(dt);

        // Jump attempt (buffered + coyote)
        if (jumpBufferTimer > 0f)
        {
            if (coyoteTimer > 0f)
            {
                DoGroundJump();
            }
            else if (onWall && !isGrounded && wallJumpLockTimer <= 0f)
            {
                DoWallJump();
            }
        }

        // Jump cut (release early)
        ApplyJumpCut();

        // Gravity shaping (apex hang + heavier fall + optional fast fall)
        ApplyGravityFeel(dt);
    }

    private void FixedDash(float dt)
    {
        dashTimer -= dt;
        if (dashTimer <= 0f)
        {
            EndDash();
            return;
        }

        // Hold constant dash velocity
        float vy = dashStopsVertical ? 0f : rb.linearVelocity.y;
        rb.linearVelocity = new Vector2(facing * dashSpeed, vy);
    }

    private void FixedWallStick(float dt)
    {
        // If player stops pushing into wall or lands -> exit
        if (isGrounded || !onWall || wallJumpLockTimer > 0f || Mathf.Abs(moveX) < 0.01f || moveX * wallDir <= 0f)
        {
            state = MoveState.Normal;
            return;
        }

        // Dash attempt while on wall
        if (dashBufferTimer > 0f && dashCooldownTimer <= 0f)
        {
            StartDash();
            return;
        }

        // Jump attempt from wall
        if (jumpBufferTimer > 0f && wallJumpLockTimer <= 0f)
        {
            DoWallJump();
            state = MoveState.Normal;
            return;
        }

        // Stick timer then slide
        if (wallStickTimer > 0f)
        {
            // Slightly damp vertical speed while sticking
            rb.linearVelocity = new Vector2(0f, Mathf.Max(rb.linearVelocity.y, -1.0f));
        }
        else
        {
            // Slide down at controlled speed (smoothly)
            float targetY = -wallSlideSpeed;
            float newY = Mathf.MoveTowards(rb.linearVelocity.y, targetY, wallSlideAccel * dt);
            rb.linearVelocity = new Vector2(0f, newY);
        }

        ApplyGravityFeel(dt, whileOnWall: true);
    }

    // =========================
    // Movement Helpers
    // =========================

    private void ApplyRun(float dt)
    {
        float targetSpeed = moveX * maxRunSpeed;
        float speedDiff = targetSpeed - rb.linearVelocity.x;

        bool isAccelerating = Mathf.Abs(targetSpeed) > 0.01f;
        bool reversing = Mathf.Sign(targetSpeed) != Mathf.Sign(rb.linearVelocity.x) && Mathf.Abs(rb.linearVelocity.x) > 0.1f;

        float accelRate;
        if (isGrounded)
            accelRate = isAccelerating ? groundAccel : groundDecel;
        else
            accelRate = isAccelerating ? airAccel : airDecel;

        if (reversing) accelRate *= turnBoost;

        float movement = accelRate * speedDiff;

        // Apply as velocity change (feels snappy)
        rb.linearVelocity = new Vector2(rb.linearVelocity.x + movement * dt, rb.linearVelocity.y);

        // Prevent micro sliding on ground when no input
        if (isGrounded && !isAccelerating && Mathf.Abs(rb.linearVelocity.x) < 0.15f)
            rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);
    }

    private void DoGroundJump()
    {
        jumpBufferTimer = 0f;
        coyoteTimer = 0f;
        apexHangTimer = 0f;

        // Replace vertical speed for consistent jump
        rb.linearVelocity = new Vector2(rb.linearVelocity.x, jumpVelocity);
    }

    private void DoWallJump()
    {
        jumpBufferTimer = 0f;
        coyoteTimer = 0f;
        apexHangTimer = 0f;

        int dirAway = -wallDir; // jump away from wall
        facing = dirAway;

        rb.linearVelocity = new Vector2(dirAway * wallJumpVelocityX, wallJumpVelocityY);

        // Lock so we don't instantly re-stick
        wallJumpLockTimer = wallJumpLockTime;
    }

    private void ApplyJumpCut()
    {
        // If player released jump while going up, cut vertical velocity
        if (!jumpHeld && rb.linearVelocity.y > minJumpVelocityForCut)
        {
            rb.linearVelocity = new Vector2(rb.linearVelocity.x, rb.linearVelocity.y * jumpCutMultiplier);
        }
    }

    private void ApplyGravityFeel(float dt, bool whileOnWall = false)
    {
        if (state == MoveState.Dashing)
        {
            rb.gravityScale = 0f;
            return;
        }

        // On wall: we usually still want some gravity, but wall slide controls descent anyway.
        float g = baseGravityScale;

        // Apex hang (tiny): when near peak and still holding jump (optional feel)
        bool nearApex = Mathf.Abs(rb.linearVelocity.y) < apexHangThreshold && rb.linearVelocity.y > -0.1f && !isGrounded && !whileOnWall;
        if (nearApex && apexHangTimer < apexHangTime)
        {
            apexHangTimer += dt;
            rb.gravityScale = 0.2f * baseGravityScale; // "hang"
            return;
        }

        // Falling -> heavier gravity
        if (rb.linearVelocity.y < 0f)
        {
            bool wantsFastFall = allowFastFall && downHeld && !isGrounded && !whileOnWall;
            g *= wantsFastFall ? fastFallGravityMultiplier : fallGravityMultiplier;
        }

        rb.gravityScale = g;
    }

    private void ClampFallSpeed()
    {
        if (state == MoveState.Dashing) return;

        float limit = maxFallSpeed;
        if (allowFastFall && downHeld && rb.linearVelocity.y < 0f && !isGrounded && state == MoveState.Normal)
            limit = maxFastFallSpeed;

        if (rb.linearVelocity.y < -limit)
            rb.linearVelocity = new Vector2(rb.linearVelocity.x, -limit);
    }

    private void StartDash()
    {
        dashBufferTimer = 0f;
        dashCooldownTimer = dashCooldown;

        state = MoveState.Dashing;
        dashTimer = dashDuration;

        // Dash feels best if it breaks out of wall stick
        wallStickTimer = 0f;

        // Optional: snap vertical to 0
        if (dashStopsVertical)
            rb.linearVelocity = new Vector2(rb.linearVelocity.x, 0f);

        // Gravity off during dash
        rb.gravityScale = 0f;
    }

    private void EndDash()
    {
        state = MoveState.Normal;
        rb.gravityScale = baseGravityScale;

        // Preserve some momentum but avoid “floaty” exits
        rb.linearVelocity = new Vector2(Mathf.Clamp(rb.linearVelocity.x, -maxRunSpeed, maxRunSpeed), rb.linearVelocity.y);
    }

    // =========================
    // Input / Checks / Timers
    // =========================

    private void ReadInputs()
    {
        // Old input system (simple). Swap to new Input System if you want.
        moveX = Input.GetAxisRaw("Horizontal");

        jumpPressed = Input.GetKeyDown(jumpKey);
        jumpHeld = Input.GetKey(jumpKey);

        dashPressed = Input.GetKeyDown(dashKey);
        downHeld = Input.GetKey(downKey) || Input.GetAxisRaw("Vertical") < -0.5f;
    }

    private void UpdateChecks()
    {
        isGrounded = groundCheck && Physics2D.OverlapCircle(groundCheck.position, groundCheckRadius, groundMask);

        onWallL = wallCheckL && Physics2D.OverlapCircle(wallCheckL.position, wallCheckRadius, groundMask);
        onWallR = wallCheckR && Physics2D.OverlapCircle(wallCheckR.position, wallCheckRadius, groundMask);

        // Coyote time refresh
        if (isGrounded)
            coyoteTimer = coyoteTime;
    }

    private void UpdateTimers(float dt)
    {
        if (!isGrounded) coyoteTimer -= dt;
        else coyoteTimer = coyoteTime;

        jumpBufferTimer -= dt;
        dashBufferTimer -= dt;

        dashCooldownTimer -= dt;
        wallJumpLockTimer -= dt;

        if (state == MoveState.WallSticking)
            wallStickTimer -= dt;

        if (jumpBufferTimer < 0f) jumpBufferTimer = 0f;
        if (dashBufferTimer < 0f) dashBufferTimer = 0f;
        if (dashCooldownTimer < 0f) dashCooldownTimer = 0f;
        if (wallJumpLockTimer < 0f) wallJumpLockTimer = 0f;
        if (wallStickTimer < 0f) wallStickTimer = 0f;
        if (coyoteTimer < 0f) coyoteTimer = 0f;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        if (groundCheck) Gizmos.DrawWireSphere(groundCheck.position, groundCheckRadius);

        Gizmos.color = Color.cyan;
        if (wallCheckL) Gizmos.DrawWireSphere(wallCheckL.position, wallCheckRadius);
        if (wallCheckR) Gizmos.DrawWireSphere(wallCheckR.position, wallCheckRadius);
    }
}