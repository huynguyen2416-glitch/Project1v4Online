using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Nhảy qua cửa sổ. Gắn vào prefab Survivor (và Hunter nếu muốn Hunter cũng nhảy được).
/// Chỉ chạy trên máy của chủ nhân nhân vật (Owner-authoritative, giống di chuyển):
///  - đứng trong vùng của VaultWindow, quay mặt về phía bên kia, nhấn phím Vault (mặc định Space);
///  - NetworkPlayerMovement khóa input và báo MoveState.Vaulting cho mọi máy chạy animation;
///  - CharacterController tắt tạm, nhân vật được đưa sang phía bên kia theo một đường vòng cung ngắn.
/// Vị trí đồng bộ bằng NetworkTransform như bình thường nên không cần RPC.
/// Không nhảy được khi: đang giải mã/mở cổng, đang Gục, đã thoát/bị loại.
/// </summary>
[RequireComponent(typeof(NetworkPlayerMovement))]
public class PlayerVault : NetworkBehaviour
{
    /// <summary>PlayerVault của người chơi trên máy này (null nếu chưa spawn).</summary>
    public static PlayerVault Local { get; private set; }


    [Header("Thời gian nhảy (giây), chỉnh cho khớp độ dài clip animation")]
    [SerializeField] float survivorDuration = 0.6f;
    [SerializeField] bool hunterCanVault = true;
    [SerializeField] float hunterDuration = 1.0f;     // Hunter qua chậm hơn

    [Header("Chuyển động")]
    [SerializeField] float arcHeight = 0.25f;          // độ nhấc người lên giữa lúc nhảy
    [SerializeField] float scanInterval = 0.1f;

    NetworkPlayerMovement move;
    CharacterController cc;
    PlayerRoleInfo role;
    SurvivorState state;
    PlayerInteractor interactor;
    PalletInteract pallet;
    // Lấy lười: BotController được thêm vào lúc chạy (BotController.Of) SAU khi object đã spawn,
    // nên không được cache ở Awake (sẽ là null mãi -> bot gửi RPC bằng id của host và bị server từ chối).
    BotController botCtrlCache;
    BotController botCtrl => botCtrlCache != null ? botCtrlCache : (botCtrlCache = GetComponent<BotController>());

    VaultWindow target;
    int targetSide;
    float nextScan;
    bool vaulting;

    public KeyCode Key => input.Keys.VaultKey;
    /// <summary>Người khác đang leo trong/cạnh vùng cửa sổ thì coi như cửa sổ đang bận (cho dung sai vì người leo đang di chuyển).</summary>
    const float OccupiedMargin = 1f;

    /// <summary>
    /// Cửa sổ có thể nhảy ngay lúc này (null nếu không có, đang nhảy, đang nghỉ 1s, hoặc có người khác đang leo cửa sổ đó).
    /// InteractUI và bot dùng để hiện lời nhắc / quyết định nhấn phím.
    /// </summary>
    public VaultWindow Target => (vaulting || target == null || !CanVault(target)) ? null : target;
    public bool IsVaulting => vaulting;

    CharacterInput input;

    void Awake()
    {
        move = GetComponent<NetworkPlayerMovement>();
        input = CharacterInput.Of(gameObject);
        cc = GetComponent<CharacterController>();
        role = GetComponent<PlayerRoleInfo>();           // có thể null
        state = GetComponent<SurvivorState>();           // Hunter không có, null
        interactor = GetComponent<PlayerInteractor>();   // có thể null
        pallet = GetComponent<PalletInteract>();         // có thể null
        botCtrlCache = GetComponent<BotController>();
    }

    public override void OnNetworkSpawn()
    {
        enabled = IsOwner || IsServer;   // server cần enabled để bot chạy sau ChangeOwnership
        if (IsLocalPlayer) Local = this;
    }

    public override void OnNetworkDespawn()
    {
        if (Local == this) Local = null;
    }

    /// <summary>Bot đang hoạt động trên server?</summary>
    bool IsBotActive => IsServer && botCtrl != null && botCtrl.State != BotController.BotState.Off;

    void Update()
    {
        if (!IsSpawned || vaulting) return;
        if (!IsOwner && !IsBotActive) return;

        bool isHunter = role != null && role.CurrentRole == Role.Hunter;
        bool canAct = isHunter ? hunterCanVault : (state == null || state.IsActive);
        if (!canAct || (move != null && move.IsLocked) || (interactor != null && interactor.IsWorking))
        {
            target = null;
            return;
        }

        // Cùng phím Space với pallet: đang trèo pallet hoặc có pallet trong vùng thì nhường cho PalletInteract,
        // tránh một lần nhấn phím chạy cả hai cú nhảy.
        if (pallet != null && (pallet.IsVaulting || pallet.Target != null))
        {
            target = null;
            return;
        }

        if (Time.time >= nextScan)
        {
            nextScan = Time.time + scanInterval;
            FindTarget();
        }

        if (target != null && input.VaultDown && CanVault(target))
            StartCoroutine(VaultRoutine(target, targetSide, isHunter));
    }

    /// <summary>Đã hết 1s nghỉ và không có ai khác đang leo cửa sổ này.</summary>
    bool CanVault(VaultWindow w)
    {
        if (!move.VaultCooldownReady) return false;
        foreach (NetworkPlayerMovement other in NetworkPlayerMovement.All)
        {
            if (other == null || other == move || !other.IsClimbing) continue;
            if (w.TryGetSide(other.transform.position, out _, out _, OccupiedMargin)) return false;
        }
        return true;
    }

    void FindTarget()
    {
        target = null;
        float best = float.MaxValue;
        foreach (VaultWindow w in VaultWindow.All)
        {
            if (w == null) continue;
            if (!w.TryGetSide(transform.position, out int side, out float dist)) continue;

            // Chỉ cần đứng trong vùng là được, không cần quay mặt về phía cửa sổ.
            if (dist < best) { best = dist; target = w; targetSide = side; }
        }
    }

    IEnumerator VaultRoutine(VaultWindow w, int side, bool isHunter)
    {
        vaulting = true;
        target = null;

        SoundManager.I?.PlaySFX(SoundManager.I.vaultWindow, transform.position);
        move.SetVaulting(true);   // khóa di chuyển thường, đứng thẳng, báo MoveState.Vaulting cho mọi máy
        cc.enabled = false;       // đi xuyên qua vật chắn của cửa sổ

        Vector3 start = transform.position;
        Vector3 end = w.GetLandingPoint(side, start.y);

        Vector3 dir = end - start;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);

        float duration = Mathf.Max(0.1f, isHunter ? hunterDuration : survivorDuration);
        bool interrupted = false;

        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            float k = t / duration;
            Vector3 p = Vector3.Lerp(start, end, k);
            p.y += Mathf.Sin(k * Mathf.PI) * arcHeight;
            transform.position = p;

            // Bị đánh gục / loại giữa chừng: dừng tại chỗ.
            if (state != null && !state.IsActive) { interrupted = true; break; }
            yield return null;
        }

        if (!interrupted) transform.position = end;

        // Nếu vừa thoát/bị loại giữa chừng thì SurvivorState đã tắt CharacterController, đừng bật lại.
        cc.enabled = state == null || !state.IsOut;
        move.SetVaulting(false);
        vaulting = false;
    }
}
