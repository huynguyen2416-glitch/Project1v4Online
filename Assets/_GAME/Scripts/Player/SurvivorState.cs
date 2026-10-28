using System.Collections;
using Unity.Netcode;
using UnityEngine;

public enum SurvivorHealth : byte
{
    Healthy = 0,     // Khỏe
    Injured = 1,     // Bị thương (trúng 1 đòn)
    Downed = 2,      // Gục (trúng đòn thứ 2): bò chậm, không làm được gì
    OnChair = 3,     // Bị trói trên ghế tên lửa, đếm ngược rồi bị loại
    Escaped = 4,     // Đã thoát: gắn cờ, không bị tấn công, không tương tác nữa
    Eliminated = 5,  // Bị loại
    Carried = 6      // Đang bị Hunter bế (tuần 3)
}

/// <summary>
/// Gắn vào prefab Survivor. Trạng thái do SERVER ghi, mọi máy đọc.
/// Chuỗi: Khỏe -> Bị thương -> Gục -> (Hunter bế) -> Trên ghế -> được cứu (về Bị thương) hoặc hết giờ (bị loại).
/// Hồi máu: Gục -> Bị thương -> Khỏe.
/// Tuần 4: máu dạng số (Khỏe 1,0; đòn thường -0,5; Gục khi hết máu); đòn mạnh -1,0 khi cổng đã có điện (cuối game) hoặc Terror Shock;
/// được tăng tốc ngắn sau khi bị đánh mà vẫn đứng được.
/// Mọi máy tự suy ra hiệu ứng (đổi màu, animation) từ trạng thái; chỉ máy chủ nhân nhân vật mới bị khóa/giảm tốc,
/// vì di chuyển là Owner-authoritative.
///
/// BỊ BẾ: chỉ máy CHỦ NHÂN của Survivor tự đặt vị trí bám theo Hunter (mỗi khung hình, trong LateUpdate).
/// NetworkTransform của Survivor vẫn BẬT như bình thường, nên các máy khác thấy Survivor đi theo Hunter qua mạng
/// và vị trí trên server luôn đúng. Tuyệt đối không tắt NetworkTransform ở máy nào.
/// </summary>
public class SurvivorState : NetworkBehaviour
{
    public static readonly System.Collections.Generic.List<SurvivorState> All = new System.Collections.Generic.List<SurvivorState>();

    public readonly NetworkVariable<byte> Value = new NetworkVariable<byte>((byte)SurvivorHealth.Healthy);
    /// <summary>Hunter đang bế (chỉ có nghĩa khi trạng thái là Carried). Dùng tham chiếu object nên không lo nhầm ClientId với NetworkObjectId.</summary>
    public readonly NetworkVariable<NetworkObjectReference> Carrier = new NetworkVariable<NetworkObjectReference>();

    [Header("Hiệu ứng trên mọi máy (đổi màu nhân vật)")]
    [SerializeField] Color injuredColor = new Color(1f, 0.6f, 0.2f);
    [SerializeField] Color downedColor = new Color(0.85f, 0.1f, 0.1f);
    [SerializeField] Color chairColor = new Color(0.6f, 0.3f, 0.85f);

    [Header("Gục (chỉ máy chủ nhân)")]
    [Range(0.1f, 1f)]
    [SerializeField] float downedSpeedFactor = 0.3f;

    [Header("Bị bế: vị trí so với Hunter (chỉ máy chủ nhân)")]
    [SerializeField] Vector3 carryOffset = new Vector3(0f, 1.5f, 0.5f);   // x phải, y lên, z ra trước

    [Header("Bị bế: độ nghiêng của body Survivor (độ, so với hướng Hunter)")]
    [Tooltip("Nghiêng tới/lui (trục X). Dương = chúi đầu xuống phía trước, ~80-90 = nằm ngang như vác trên vai. Đảo dấu nếu model nghiêng ngược.")]
    [SerializeField] float carryTiltPitch = 75f;
    [Tooltip("Nghiêng sang trái/phải (trục Z). Dương = nghiêng sang trái của Hunter.")]
    [SerializeField] float carryTiltRoll = 0f;
    [Tooltip("Xoay quanh trục đứng (trục Y), để chỉnh body quay ngang/quay lưng so với Hunter.")]
    [SerializeField] float carryTiltYaw = 0f;
    [Tooltip("Độ cao (so với chân) của điểm làm tâm xoay khi nghiêng, ~ giữa thân. 0 = xoay quanh chân. Chỉnh để body không bị văng ra khi nghiêng.")]
    [SerializeField] float carryTiltPivotHeight = 0.9f;

    [Header("Server: sát thương (máu tổng là 1,0)")]
    [Tooltip("Sau khi trúng đòn, trong ngần này giây không bị trúng thêm.")]
    [SerializeField] float hitImmunity = 1f;
    [Tooltip("Đòn thường trừ bao nhiêu máu (0,5 = hai đòn thì Gục).")]
    [SerializeField] float hitDamage = 0.5f;
    [Tooltip("Đòn mạnh trừ bao nhiêu máu: dùng khi cổng đã có điện (cuối game) và khi Terror Shock.")]
    [SerializeField] float strongDamage = 1f;
    [Tooltip("Đánh trúng Survivor đang nhảy cửa sổ, giải mã máy, hồi máu đồng đội hoặc cứu người khỏi ghế thì hạ gục ngay.")]
    [SerializeField] bool terrorShockEnabled = true;

    [Header("Tăng tốc sau khi bị đánh mà chưa Gục (chỉ máy chủ nhân)")]
    [SerializeField] float hitBoostMultiplier = 1.4f;
    [SerializeField] float hitBoostSeconds = 2f;

    [Header("Hiệu ứng Terror Shock")]
    [SerializeField] Color terrorShockColor = new Color(1f, 0f, 1f);
    [SerializeField] float terrorShockFlashSeconds = 0.45f;

    [Header("Test nhanh (chỉ Host tự thử)")]
    [Tooltip("Bật để thử trạng thái: F5 = bị đánh 1 đòn, F6 = hồi máu 1 bậc, F7 = về Khỏe.")]
    [SerializeField] bool debugKeys = false;

    static readonly int HealthParam = Animator.StringToHash("healthState");
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    Renderer[] renderers;
    MaterialPropertyBlock block;
    Animator animator;
    bool hasHealthParam;
    NetworkPlayerMovement move;
    CharacterController cc;

    float lastHitTime = -999f;       // server
    float hp = 1f;                   // server: máu hiện tại (1 = Khỏe, 0,5 = Bị thương, 0 = Gục)
    Vector3 snapPos;                 // owner: vị trí cần dịch chuyển tới (ghế / chỗ được cứu / chỗ bị thả)
    float snapYaw;
    bool snapPending;
    Transform carrierTransform;      // cache của Carrier, chỉ dùng ở máy chủ nhân

    public SurvivorHealth Current => (SurvivorHealth)Value.Value;

    /// <summary>Còn hành động được (giải mã, mở cổng, nhảy hầm, cứu, hồi máu...).</summary>
    public bool IsActive => Current == SurvivorHealth.Healthy || Current == SurvivorHealth.Injured;

    /// <summary>Đã rời trận (thoát hoặc bị loại): không còn bị tính là "còn sống trên bản đồ".</summary>
    public bool IsOut => Current == SurvivorHealth.Escaped || Current == SurvivorHealth.Eliminated;

    void Awake()
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        block = new MaterialPropertyBlock();
        animator = GetComponentInChildren<Animator>();
        move = GetComponent<NetworkPlayerMovement>();
        cc = GetComponent<CharacterController>();

        if (animator != null)
        {
            foreach (AnimatorControllerParameter p in animator.parameters)
                if (p.nameHash == HealthParam) { hasHealthParam = true; break; }
        }

        // Ẩn toàn bộ renderer ngay khi tạo ra, tránh flash màu xanh lúc shader chưa compile.
        // Sẽ được bật lại sau 1 frame trong OnNetworkSpawn.
        foreach (Renderer r in renderers) if (r != null) r.enabled = false;
    }

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        Value.OnValueChanged += OnChanged;
        // Chờ 1 frame để shader kịp compile rồi mới Apply() để tránh flash màu xanh.
        StartCoroutine(ApplyNextFrame());
    }

    IEnumerator ApplyNextFrame()
    {
        yield return null;   // 1 frame
        if (IsSpawned) Apply();
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        Value.OnValueChanged -= OnChanged;
    }

    void OnChanged(byte oldValue, byte newValue) => Apply();

    // ======================= SERVER =======================

    public void ServerSet(SurvivorHealth s)
    {
        if (!IsServer) return;
        switch (s)   // giữ máu dạng số khớp với trạng thái
        {
            case SurvivorHealth.Healthy: hp = 1f; break;
            case SurvivorHealth.Injured: hp = 0.5f; break;
            case SurvivorHealth.Downed: hp = 0f; break;
        }
        Value.Value = (byte)s;
    }

    /// <summary>
    /// Hunter đánh trúng. Đòn thường trừ 0,5 máu (Khỏe -> Bị thương -> Gục). Đòn MẠNH trừ 1,0 máu (Gục ngay) khi:
    ///  - cổng đã có điện (cuối game), hoặc
    ///  - Terror Shock: Survivor đang nhảy cửa sổ, giải mã máy, hồi máu đồng đội hoặc cứu người khỏi ghế.
    /// Bị đánh mà chưa Gục thì được tăng tốc ngắn. Trả về true nếu có tác dụng.
    /// </summary>
    public bool ServerHit()
    {
        if (!IsServer || !IsActive || Time.time - lastHitTime < hitImmunity) return false;

        float damage = hitDamage;
        bool terrorShock = false;
        MatchManager mm = MatchManager.Instance;
        if (mm != null && mm.GatesPowered.Value)
        {
            damage = strongDamage;                                   // cuối game: một đòn trừ 1 máu
        }
        else if (terrorShockEnabled && IsBusyForTerrorShock())
        {
            damage = strongDamage;
            terrorShock = true;
        }

        lastHitTime = Time.time;
        float newHp = Mathf.Max(0f, hp - damage);
        ServerSet(newHp <= 0f ? SurvivorHealth.Downed : (newHp < 1f ? SurvivorHealth.Injured : SurvivorHealth.Healthy));

        if (terrorShock) TerrorShockClientRpc();
        if (Current == SurvivorHealth.Injured) BoostClientRpc(hitBoostMultiplier, hitBoostSeconds, OwnerOnly());
        return true;
    }

    // Terror Shock: đang nhảy cửa sổ, hoặc đang dùng thứ có TerrorShockOnUse (giải mã máy, hồi máu, cứu người khỏi ghế).
    bool IsBusyForTerrorShock()
    {
        if (move != null && move.IsVaultingNet) return true;

        foreach (NetInteractable it in NetInteractable.All)
            if (it.TerrorShockOnUse && it.IsUsedBy(OwnerClientId)) return true;
        return false;
    }

    /// <summary>Hồi máu 1 bậc: Gục -> Bị thương, Bị thương -> Khỏe.</summary>
    public bool ServerHeal()
    {
        if (!IsServer) return false;
        switch (Current)
        {
            case SurvivorHealth.Downed: ServerSet(SurvivorHealth.Injured); return true;
            case SurvivorHealth.Injured: ServerSet(SurvivorHealth.Healthy); return true;
            default: return false;
        }
    }

    /// <summary>Hunter bắt đầu bế người đang Gục.</summary>
    public bool ServerPickUp(NetworkObject hunter)
    {
        if (!IsServer || hunter == null || Current != SurvivorHealth.Downed) return false;
        Carrier.Value = hunter;
        ServerSet(SurvivorHealth.Carried);
        return true;
    }

    /// <summary>Hunter thả người đang bế: về lại trạng thái Gục tại dropPos (chỗ đã được HunterCarry kiểm tra không xuyên tường).</summary>
    public void ServerDrop(Vector3 dropPos, float dropYaw)
    {
        if (!IsServer || Current != SurvivorHealth.Carried) return;
        Carrier.Value = default;
        SendSnap(dropPos, dropYaw);   // máy chủ nhân dịch chuyển tới đúng vị trí này
        ServerSet(SurvivorHealth.Downed);
    }

    /// <summary>Đặt lên ghế tên lửa: dịch chuyển tới ghế rồi khóa di chuyển.</summary>
    public void ServerPutOnChair(Vector3 pos, float yaw)
    {
        if (!IsServer) return;
        Carrier.Value = default;
        SendSnap(pos, yaw);
        ServerSet(SurvivorHealth.OnChair);
    }

    /// <summary>Được cứu khỏi ghế: đứng ở chỗ pos, trạng thái Bị thương.</summary>
    public void ServerRescue(Vector3 pos, float yaw)
    {
        if (!IsServer || Current != SurvivorHealth.OnChair) return;
        SendSnap(pos, yaw);
        ServerSet(SurvivorHealth.Injured);
    }

    ClientRpcParams OwnerOnly()
    {
        return new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };
    }

    void SendSnap(Vector3 pos, float yaw)
    {
        SnapClientRpc(pos, yaw, OwnerOnly());
    }

    // ======================= CLIENT =======================

    [ClientRpc]
    void SnapClientRpc(Vector3 pos, float yaw, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        snapPos = pos;
        snapYaw = yaw;
        snapPending = true;
        TrySnap();
    }

    // Chỉ máy chủ nhân: tăng tốc ngắn sau khi bị đánh.
    [ClientRpc]
    void BoostClientRpc(float multiplier, float seconds, ClientRpcParams rpcParams = default)
    {
        if (IsOwner && move != null) move.AddSpeedBoost(multiplier, seconds);
    }

    // Mọi máy: nhấp nháy màu báo Terror Shock.
    [ClientRpc]
    void TerrorShockClientRpc()
    {
        StartCoroutine(FlashRoutine());
    }

    IEnumerator FlashRoutine()
    {
        SetTint(terrorShockColor);
        yield return new WaitForSeconds(terrorShockFlashSeconds);
        ApplyTint(Current);
    }

    // Chỉ dịch chuyển khi không còn bị bế, để tránh bị "kéo ngược" về chỗ Hunter do thứ tự gói tin.
    void TrySnap()
    {
        if (!snapPending || move == null || Current == SurvivorHealth.Carried) return;
        move.SnapTo(snapPos, snapYaw);
        snapPending = false;
    }

    void Update()
    {
        if (!IsSpawned) return;

        if (IsOwner) TrySnap();

        if (debugKeys && IsServer && IsOwner)
        {
            if (Input.GetKeyDown(KeyCode.F5)) ServerHit();
            if (Input.GetKeyDown(KeyCode.F6)) ServerHeal();
            if (Input.GetKeyDown(KeyCode.F7)) ServerSet(SurvivorHealth.Healthy);
        }
    }

    // LateUpdate: chạy sau khi NetworkTransform đã nội suy vị trí Hunter trong khung hình này, nên Survivor bám sát, ít giật.
    void LateUpdate()
    {
        if (IsSpawned && IsOwner) FollowCarrier();
    }

    // Đang bị bế: máy chủ nhân tự đặt vị trí bám theo Hunter (di chuyển là Owner-authoritative nên chỉ owner đặt được).
    void FollowCarrier()
    {
        if (Current != SurvivorHealth.Carried) return;

        if (carrierTransform == null)
        {
            if (!Carrier.Value.TryGet(out NetworkObject hunter)) return;   // chưa nhận được biến mạng, thử lại khung sau
            carrierTransform = hunter.transform;
        }

        Transform h = carrierTransform;
        Vector3 basePos = h.position + h.rotation * carryOffset;
        Quaternion rot = h.rotation * Quaternion.Euler(carryTiltPitch, carryTiltYaw, carryTiltRoll);
        // Nghiêng quanh giữa thân (không phải quanh chân) để body nằm đúng chỗ trên vai Hunter.
        Vector3 pivot = Vector3.up * carryTiltPivotHeight;
        transform.SetPositionAndRotation(basePos + pivot - rot * pivot, rot);
    }

    // Chạy trên MỌI máy mỗi khi trạng thái đổi.
    void Apply()
    {
        SurvivorHealth s = Current;
        bool gone = IsOut;
        if (s != SurvivorHealth.Carried) carrierTransform = null;

        // Đã thoát/bị loại: ẩn model, khóa di chuyển. Server cũng dùng IsOut để bỏ qua khi tấn công.
        foreach (Renderer r in renderers)
            if (r != null) r.enabled = !gone;
        if (move != null) move.enabled = !gone;

        ApplyTint(s);
        if (animator != null && hasHealthParam) animator.SetInteger(HealthParam, (int)s);

        if (!IsOwner) return;

        if (move != null)
        {
            move.SetLocked(this, s == SurvivorHealth.OnChair || s == SurvivorHealth.Carried);
            move.SetSpeedMultiplier(s == SurvivorHealth.Downed ? downedSpeedFactor : 1f);
        }
        // Đang bị bế hoặc trên ghế thì tắt CharacterController để tự đặt vị trí được.
        // KHÔNG đụng tới NetworkTransform: tắt nó ở bất kỳ máy nào sẽ làm vị trí các máy lệch nhau.
        if (cc != null) cc.enabled = !(gone || s == SurvivorHealth.Carried || s == SurvivorHealth.OnChair);

        TrySnap();
    }

    void ApplyTint(SurvivorHealth s)
    {
        Color c;
        switch (s)
        {
            case SurvivorHealth.Injured: c = injuredColor; break;
            case SurvivorHealth.Downed:
            case SurvivorHealth.Carried: c = downedColor; break;
            case SurvivorHealth.OnChair: c = chairColor; break;
            default: c = Color.white; break;
        }

        SetTint(c);
    }

    void SetTint(Color c)
    {
        foreach (Renderer r in renderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(block);
            block.SetColor(BaseColorId, c);   // URP Lit
            block.SetColor(ColorId, c);       // shader cũ
            r.SetPropertyBlock(block);
        }
    }
}
