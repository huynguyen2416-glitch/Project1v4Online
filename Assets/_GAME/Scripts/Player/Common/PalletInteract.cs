using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Đập / trèo / phá pallet. Gắn vào prefab SURVIVOR và HUNTER (cùng 1 script, tự phân biệt vai).
/// Chạy giống PlayerVault (cửa sổ): chỉ trên máy chủ nhân, quét pallet mỗi scanInterval giây, phải quay mặt về phía bên kia,
/// không hiện giao diện riêng mà để InteractUI đọc PalletInteract.Local.Prompt.
///
/// PHÍM: SPACE cho mọi hành động (cố định trong code, không phụ thuộc giá trị lưu trong prefab).
///   SURVIVOR: pallet đứng → Space = Đập pallet. Pallet nằm → Space = Trèo qua.
///   HUNTER:   pallet nằm  → Space = Phá pallet (anim ~1.5 giây, Hunter bị khóa trong lúc đó).
/// Không dùng được khi: đang giải mã / mở cổng, đang bị khóa (đánh, bế...), Survivor đã gục / bị bế / lên ghế / bị loại,
/// Hunter đang bế người, trận đã kết thúc.
///
/// Cùng nằm trên prefab với PlayerVault (cùng phím Space): script này ưu tiên pallet nếu đang có pallet trong vùng,
/// còn PlayerVault tự nhường (xem PlayerVault.Update). Hai script không bao giờ cùng chạy một cú nhảy.
/// </summary>
[RequireComponent(typeof(NetworkPlayerMovement))]
public class PalletInteract : NetworkBehaviour
{
    /// <summary>PalletIntera   ct của người chơi trên máy này (null nếu chưa spawn).</summary>
    public static PalletInteract Local { get; private set; }

    public KeyCode Key => input.Keys.PalletKey;

    [Header("Quét")]
    [Tooltip("1 = phải nhìn thẳng vào pallet, 0 = chỉ cần không quay lưng. Gợi ý 0.3 (giống PlayerVault).")]
    [Range(-1f, 1f)] [SerializeField] float minFacing = 0.3f;
    [SerializeField] float scanInterval = 0.1f;

    NetworkPlayerMovement move;
    CharacterController cc;
    PlayerRoleInfo role;
    SurvivorState state;
    HunterCarry carry;
    PlayerInteractor interactor;
    PlayerVault vault;
    // Lấy lười: BotController được thêm vào lúc chạy (BotController.Of) SAU khi object đã spawn,
    // nên không được cache ở Awake (sẽ là null mãi -> bot gửi RPC bằng id của host và bị server từ chối).
    BotController botCtrlCache;
    BotController botCtrl => botCtrlCache != null ? botCtrlCache : (botCtrlCache = GetComponent<BotController>());

    Pallet target;
    int targetSide;
    float nextScan;
    bool vaulting;

    /// <summary>Pallet có thể tương tác ngay lúc này (null nếu không có hoặc đang trèo). PlayerVault dùng để nhường.</summary>
    public Pallet Target => (vaulting || target == null || IsClimbBlocked(target)) ? null : target;

    /// <summary>Người khác đang leo trong/cạnh vùng pallet thì coi như pallet đang bận (cho dung sai vì người leo đang di chuyển).</summary>
    const float OccupiedMargin = 1f;

    /// <summary>
    /// Survivor muốn TRÈO pallet đã nằm nhưng chưa được: còn đang nghỉ 1s, hoặc có người khác đang trèo pallet đó.
    /// Đập pallet đang đứng và Hunter phá pallet không bị chặn bởi luật này.
    /// </summary>
    bool IsClimbBlocked(Pallet p)
    {
        if (IsHunter || !p.IsDropped || p.IsBreaking.Value) return false;
        if (!move.VaultCooldownReady) return true;
        foreach (NetworkPlayerMovement other in NetworkPlayerMovement.All)
        {
            if (other == null || other == move || !other.IsClimbing) continue;
            if (p.TryGetSide(other.transform.position, out _, out _, OccupiedMargin)) return true;
        }
        return false;
    }
    public bool IsVaulting => vaulting;

    bool IsHunter => role != null && role.CurrentRole == Role.Hunter;

    /// <summary>Lời nhắc cho InteractUI (null nếu không có gì để làm).</summary>
    public string Prompt
    {
        get
        {
            Pallet p = Target;
            if (p == null || p.IsDestroyed) return null;
            if (IsHunter) return $"[{Key}] Phá pallet";
            return p.IsStanding ? $"[{Key}] Đập pallet" : $"[{Key}] Trèo qua pallet";
        }
    }

    CharacterInput input;

    void Awake()
    {
        move = GetComponent<NetworkPlayerMovement>();
        input = CharacterInput.Of(gameObject);
        cc = GetComponent<CharacterController>();
        role = GetComponent<PlayerRoleInfo>();
        state = GetComponent<SurvivorState>();           // Hunter không có, null
        carry = GetComponent<HunterCarry>();             // Survivor không có, null
        interactor = GetComponent<PlayerInteractor>();   // có thể null
        vault = GetComponent<PlayerVault>();             // có thể null
        botCtrlCache = GetComponent<BotController>();
    }

    public override void OnNetworkSpawn()
    {
        enabled = IsOwner || IsServer;   // server cần enabled để bot chạy
        if (IsLocalPlayer) Local = this;
    }

    public override void OnNetworkDespawn()
    {
        if (Local == this) Local = null;
        target = null;
    }

    /// <summary>Bot đang hoạt động trên server?</summary>
    bool IsBotActive => IsServer && botCtrl != null && botCtrl.State != BotController.BotState.Off;

    void Update()
    {
        if (!IsSpawned || vaulting) return;
        if (!IsOwner && !IsBotActive) return;

        // Đang nhảy cửa sổ thì không làm gì với pallet.
        if (vault != null && vault.IsVaulting) { target = null; return; }

        bool isHunter = IsHunter;
        bool canAct = isHunter
            ? (carry == null || !carry.IsCarrying)
            : (state == null || state.IsActive);

        if (!canAct || MatchManager.Ended ||
            (move != null && move.IsLocked) ||
            (interactor != null && interactor.IsWorking))
        {
            target = null;
            return;
        }

        if (Time.time >= nextScan)
        {
            nextScan = Time.time + scanInterval;
            FindTarget(isHunter);
        }

        if (target != null && input.PalletDown) Act(isHunter);
    }

    void FindTarget(bool isHunter)
    {
        target = null;
        float best = float.MaxValue;
        foreach (Pallet p in Pallet.All)
        {
            if (p == null || p.IsDestroyed) continue;

            if (isHunter)
            {
                // Hunter chỉ phá pallet đã nằm xuống và chưa đang bị phá.
                if (!p.IsDropped || p.IsBreaking.Value) continue;
            }
            else if (p.IsDropped && p.IsBreaking.Value)
            {
                continue;   // pallet đang vỡ: Survivor không trèo được
            }

            if (!p.TryGetSide(transform.position, out int side, out float dist)) continue;

            // Phải đang quay mặt về phía bên kia pallet.
           if (isHunter && Vector3.Dot(transform.forward, p.JumpDirection(side)) < minFacing) continue;

            if (dist < best) { best = dist; target = p; targetSide = side; }
        }
    }

    void Act(bool isHunter)
    {
        Pallet p = target;

        if (isHunter)
        {
            if (!p.IsDropped || p.IsBreaking.Value) return;
            // Khóa Hunter NGAY tại máy chủ nhân trong đúng thời gian anim phá.
            move.LockFor(p.BreakDuration);

            if (IsBotActive)
                p.ServerBreak(botCtrl.OriginalClientId);
            else
                p.BreakServerRpc();
        }
        else if (p.IsStanding)
        {
            if (IsBotActive)
                p.ServerDrop(botCtrl.OriginalClientId);
            else
                p.DropServerRpc();
        }
        else if (p.IsDropped && !p.IsBreaking.Value)
        {
            if (IsClimbBlocked(p)) return;   // đang nghỉ 1s hoặc có người khác đang trèo
            StartCoroutine(VaultRoutine(p, targetSide));
        }
    }

    IEnumerator VaultRoutine(Pallet p, int side)
    {
        vaulting = true;
        target = null;

        move.SetVaulting(true);   // khóa di chuyển thường, báo MoveState.Vaulting cho mọi máy
        if (cc != null) cc.enabled = false;

        Vector3 start = transform.position;
        Vector3 end = p.GetLandingPoint(side, start.y);

        Vector3 dir = end - start;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);

        float duration = Mathf.Max(0.1f, p.VaultDuration);
        float arc = p.VaultArcHeight;
        bool interrupted = false;

        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            float k = t / duration;
            Vector3 pos = Vector3.Lerp(start, end, k);
            pos.y += Mathf.Sin(k * Mathf.PI) * arc;
            transform.position = pos;

            // Pallet bị phá / biến mất, hoặc bị đánh gục / loại giữa chừng: dừng tại chỗ.
            if (p == null || p.IsDestroyed) { interrupted = true; break; }
            if (state != null && !state.IsActive) { interrupted = true; break; }

            yield return null;
        }

        if (!interrupted) transform.position = end;

        // Nếu vừa thoát / bị loại giữa chừng thì SurvivorState đã tắt CharacterController, đừng bật lại.
        if (cc != null) cc.enabled = state == null || !state.IsOut;
        move.SetVaulting(false);
        vaulting = false;
    }
}
