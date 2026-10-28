using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Di chuyển người chơi qua mạng (Owner-authoritative).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class NetworkPlayerMovement : NetworkBehaviour
{
    public enum MoveState : byte { Idle = 0, Running = 1, CrouchIdle = 2, CrouchWalk = 3, Vaulting = 4 }

    public static event System.Action<MoveState> OnLocalStateChanged;

    [Header("Tốc độ")]
    [SerializeField] float runSpeed = 4f;
    [SerializeField] float crouchSpeed = 2.5f;
    [SerializeField] float turnSpeed = 720f;
    [SerializeField] float gravity = -20f;

    [Header("Phím Ngồi (Bấm C để Bật/Tắt)")]
    [SerializeField] KeyCode crouchKey = KeyCode.C;

    [Header("Crouch")]
    [Range(0.4f, 0.9f)]
    [SerializeField] float crouchHeightRatio = 0.6f;

    [Header("Animation")]
    [SerializeField] Animator animator;

    [Header("Vị trí spawn")]
    [SerializeField] Vector3 spawnOrigin = new Vector3(0f, 0.1f, 0f);
    [SerializeField] float spawnSpacing = 2f;

    readonly NetworkVariable<byte> netState = new NetworkVariable<byte>(
        0, NetworkVariableBase.DefaultReadPerm, NetworkVariableWritePermission.Owner);

    static readonly int MoveStateParam = Animator.StringToHash("moveState");

    CharacterController cc;
    Transform cam;
    float standHeight;
    Vector3 standCenter;
    float verticalVelocity;
    bool crouching;
    bool capsuleCrouched;
    int lastShownState = -1;

    float timedLockUntil;
    readonly System.Collections.Generic.HashSet<object> lockers = new System.Collections.Generic.HashSet<object>();
    float speedMultiplier = 1f;

    public bool IsVaulting { get; private set; }
    float boostMultiplier = 1f;
    float boostUntil;

    //Tăng tốc tạm thời (ví dụ sau khi bị đánh). Chỉ gọi trên máy owner
    public void AddSpeedBoost(float multiplier, float seconds)
    {
        boostMultiplier = Mathf.Max(1f, multiplier);
        boostUntil = Time.time + seconds;
    }

    /// <summary>Đang di chuyển (chạy hoặc đi ngồi thấp). Đọc từ biến đã đồng bộ nên máy nào cũng dùng được.</summary>
    public bool IsMovingNet => netState.Value == (byte)MoveState.Running || netState.Value == (byte)MoveState.CrouchWalk;

    /// <summary>Đang chạy (không phải ngồi thấp).</summary>
    public bool IsRunningNet => netState.Value == (byte)MoveState.Running;

    /// <summary>Đang nhảy cửa sổ. Server dùng để xét Terror Shock.</summary>
    public bool IsVaultingNet => netState.Value == (byte)MoveState.Vaulting;
    /// <summary>Đang bị khóa di chuyển (đang làm động tác, bị bế, trên ghế...). Các script hành động khác (nhảy cửa sổ...) cũng phải tôn trọng.</summary>
    public bool IsLocked => lockers.Count > 0 || Time.time < timedLockUntil;

    /// <summary>Khóa di chuyển và thao tác trong seconds giây (đánh, bế, thả...). Chỉ gọi trên máy chủ nhân.</summary>
    public void LockFor(float seconds)
    {
        if (seconds <= 0f) return;
        bool was = IsLocked;
        timedLockUntil = Mathf.Max(timedLockUntil, Time.time + seconds);
        if (!was) StopMoving();
    }

    /// <summary>Mỗi nơi khóa tự khai báo (truyền this) nên mở khóa của nơi này không mở nhầm khóa của nơi khác.</summary>
    public void SetLocked(object source, bool on)
    {
        bool was = IsLocked;
        if (on) lockers.Add(source); else lockers.Remove(source);
        if (!was && IsLocked) StopMoving();
    }

    public void SetSpeedMultiplier(float m) { speedMultiplier = Mathf.Max(0f, m); }

    public void SetVaulting(bool on)
    {
        if (!IsOwner || IsVaulting == on) return;
        IsVaulting = on;
        if (on)
        {
            StopMoving();
            if (IsSpawned) netState.Value = (byte)MoveState.Vaulting;
        }
        else
        {
            verticalVelocity = 0f;
            if (IsSpawned) netState.Value = (byte)MoveState.Idle;
        }
    }

    public void SnapTo(Vector3 pos, float yaw)
    {
        bool wasEnabled = cc.enabled;
        cc.enabled = false;
        Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
        transform.SetPositionAndRotation(pos, rot);
        NetworkTransform nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.Teleport(pos, rot, transform.localScale);
        cc.enabled = wasEnabled;
        verticalVelocity = 0f;
    }

    void StopMoving()
    {
        crouching = false;
        if (capsuleCrouched) SetCapsule(false);
        if (IsOwner && IsSpawned && netState.Value != (byte)MoveState.Idle) netState.Value = (byte)MoveState.Idle;
    }

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        standHeight = cc.height;
        standCenter = cc.center;
    }

    // Đếm số người đã spawn trong scene hiện tại để chia slot không trùng nhau.
    static int spawnCounter;

    // Reset bộ đếm mỗi khi scene mới được load (Lobby → Match → Lobby...).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void HookSceneReset()
    {
        spawnCounter = 0;
        SceneManager.sceneLoaded += (_, _2) => spawnCounter = 0;
    }

    public override void OnNetworkSpawn()
    {
        cc.enabled = false;
        if (!IsOwner) return;

        int slot = spawnCounter++ % 5;
        Vector3 pos = spawnOrigin + Vector3.right * (spawnSpacing * slot);
        transform.position = pos;
        cc.enabled = true;

        if (Camera.main != null) cam = Camera.main.transform;

        var follow = FindFirstObjectByType<ThirdPersonCamera>();
        if (follow != null) follow.target = transform;
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (IsOwner) OwnerUpdate();
        ShowAnimation();
    }

    void OwnerUpdate()
    {
        if (cam == null && Camera.main != null) cam = Camera.main.transform;

        if (IsLocked || IsVaulting) return;

        // --- Crouch (Bấm C 1 lần là ngồi, bấm lại 1 lần là đứng) ---
        if (Input.GetKeyDown(crouchKey))
        {
            crouching = !crouching;
        }
        if (crouching != capsuleCrouched) SetCapsule(crouching);

        // --- Hướng di chuyển ---
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        Vector3 fwd = cam != null ? cam.forward : transform.forward;
        Vector3 right = cam != null ? cam.right : transform.right;
        fwd.y = 0f; right.y = 0f;
        fwd.Normalize(); right.Normalize();

        Vector3 dir = fwd * v + right * h;
        bool moving = dir.sqrMagnitude > 0.0001f;
        if (moving)
        {
            dir.Normalize();
            Quaternion target = Quaternion.LookRotation(dir, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
        }

        // --- Di chuyển ---
        float speed = (crouching ? crouchSpeed : runSpeed) * speedMultiplier * (Time.time < boostUntil ? boostMultiplier : 1f);
        if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f;
        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = (moving ? dir * speed : Vector3.zero) + Vector3.up * verticalVelocity;
        cc.Move(velocity * Time.deltaTime);

        // --- Đồng bộ trạng thái Animation ---
        MoveState state = crouching
            ? (moving ? MoveState.CrouchWalk : MoveState.CrouchIdle)
            : (moving ? MoveState.Running : MoveState.Idle);

        if (netState.Value != (byte)state) netState.Value = (byte)state;
    }

    void SetCapsule(bool crouch)
    {
        capsuleCrouched = crouch;
        float h = crouch ? standHeight * crouchHeightRatio : standHeight;
        float bottom = standCenter.y - standHeight * 0.5f;
        cc.height = h;
        cc.center = new Vector3(standCenter.x, bottom + h * 0.5f, standCenter.z);
    }

    void ShowAnimation()
    {
        int s = netState.Value;
        if (s == lastShownState) return;
        lastShownState = s;

        if (animator != null) animator.SetInteger(MoveStateParam, s);
        if (IsOwner) OnLocalStateChanged?.Invoke((MoveState)s);
    }
}