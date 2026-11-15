using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Hunter bế người đang Gục, thả xuống hoặc đặt lên ghế tên lửa.
/// Bế / thả / đặt đều khóa Hunter (không đi, không nhảy cửa sổ, không đánh) trong lockOnPickup / lockOnDrop / lockOnPlace giây.
/// Survivor bị bế tự bám theo Hunter, xem SurvivorState.FollowCarrier.
/// </summary>
[RequireComponent(typeof(PlayerRoleInfo))]
[RequireComponent(typeof(NetworkPlayerMovement))]
[RequireComponent(typeof(HunterAttack))]
public class HunterCarry : NetworkBehaviour
{
    public static HunterCarry Local { get; private set; }


    [Header("Khóa sau khi bế / thả / đặt lên ghế (giây). 0 = không khóa. Chỉnh cho bằng độ dài animation.")]
    [SerializeField] float lockOnPickup = 1.0f;
    [SerializeField] float lockOnDrop = 0.8f;
    [SerializeField] float lockOnPlace = 1.0f;

    [Header("Bế người")]
    [SerializeField] float pickupRange = 2f;
    [SerializeField] float chairRange = 2.5f;
    [Range(0.3f, 1f)][SerializeField] float carrySpeedFactor = 0.85f;

    [Header("Vật cản")]
    [Tooltip("Độ cao điểm bắn tia kiểm tra tường (ngang ngực), tính từ chân.")]
    [SerializeField] float eyeHeight = 1.0f;
    [Tooltip("Chênh lệch độ cao tối đa để còn bế / đặt ghế được.")]
    [SerializeField] float maxHeightDiff = 1.5f;
    [Tooltip("Layer được coi là vật cản. Bỏ tick layer nào không muốn chặn.")]
    [SerializeField] LayerMask blockMask = ~0;
    [Tooltip("Khi thả người, chừa khoảng cách này khỏi tường (mét).")]
    [SerializeField] float dropClearance = 0.5f;

    [Header("Animation")]
    [SerializeField] Animator animator;

    static readonly int PickupParam = Animator.StringToHash("pickup_survivor");
    static readonly int DropParam = Animator.StringToHash("drop_survivor");
    static readonly int IsCarryingParam = Animator.StringToHash("isCarrying");

    const byte ActionPickup = 0, ActionDrop = 1, ActionPlace = 2;

    readonly NetworkVariable<bool> isCarrying = new NetworkVariable<bool>(false);

    PlayerRoleInfo role;
    NetworkPlayerMovement move;
    HunterAttack attack;
    BotController botCtrl;
    bool hasPickup, hasDrop, hasIsCarrying;

    SurvivorState carried;   // server
    float nextScan;
    bool nearDowned, nearChair;
    SurvivorState nearDownedTarget;
    RocketChair nearChairTarget;
    string prompt;

    public bool IsCarrying => isCarrying.Value;
    public string Prompt => prompt;

    /// <summary>
    /// Object mà lời nhắc hiện tại nói tới: ghế tên lửa (đang bế) hoặc người đang gục (chưa bế).
    /// null nếu lời nhắc không gắn với object nào (ví dụ đang bế mà chưa tới ghế). BotBrain dùng để biết khi nào được bấm phím bế / đặt ghế.
    /// </summary>
    public Component PromptTarget =>
        string.IsNullOrEmpty(prompt) ? null : (isCarrying.Value ? (Component)nearChairTarget : nearDownedTarget);

    CharacterInput input;

    void Awake()
    {
        role = GetComponent<PlayerRoleInfo>();
        input = CharacterInput.Of(gameObject);
        move = GetComponent<NetworkPlayerMovement>();
        attack = GetComponent<HunterAttack>();
        botCtrl = GetComponent<BotController>();
        if (animator == null) animator = GetComponentInChildren<Animator>();

        if (animator == null) return;
        foreach (AnimatorControllerParameter p in animator.parameters)
        {
            if (p.nameHash == PickupParam) hasPickup = true;
            if (p.nameHash == DropParam) hasDrop = true;
            if (p.nameHash == IsCarryingParam) hasIsCarrying = true;
        }
    }

    public override void OnNetworkSpawn()
    {
        isCarrying.OnValueChanged += OnCarryChanged;
        if (IsOwner)
        {
            Local = this;
            move.SetSpeedMultiplier(isCarrying.Value ? carrySpeedFactor : 1f);
        }
        if (animator != null && hasIsCarrying) animator.SetBool(IsCarryingParam, isCarrying.Value);
    }

    public override void OnNetworkDespawn()
    {
        isCarrying.OnValueChanged -= OnCarryChanged;
        if (Local == this) Local = null;

        // Hunter rớt mạng giữa lúc đang bế: thả người xuống tại chỗ.
        if (IsServer && carried != null) carried.ServerDrop(transform.position, transform.eulerAngles.y);
    }

    /// <summary>
    /// SERVER: Survivor đang bị bế vừa nối lại mạng. Object cũ (bot) sắp bị despawn nên giao Survivor MỚI cho Hunter bế tiếp,
    /// nếu không Update() thấy carried == null và Hunter tự mất người.
    /// </summary>
    public bool ServerAdoptCarried(SurvivorState oldState, SurvivorState newState)
    {
        if (!IsServer || newState == null || carried != oldState) return false;
        carried = newState;
        return true;
    }

    void OnCarryChanged(bool oldValue, bool newValue)
    {
        if (IsOwner) move.SetSpeedMultiplier(newValue ? carrySpeedFactor : 1f);
        if (animator != null && hasIsCarrying) animator.SetBool(IsCarryingParam, newValue);
    }

    /// <summary>Bot đang hoạt động trên server?</summary>
    bool IsBotActive => IsServer && botCtrl != null && botCtrl.State != BotController.BotState.Off;

    void Update()
    {
        if (!IsSpawned) return;

        // Server: người bị bế đổi trạng thái từ nơi khác (rớt mạng...) thì thôi bế.
        if (IsServer && isCarrying.Value && (carried == null || carried.Current != SurvivorHealth.Carried))
        {
            carried = null;
            isCarrying.Value = false;
        }

        bool isBotNow = IsBotActive;
        if (!IsOwner && !isBotNow) return;
        if (role.CurrentRole != Role.Hunter) return;

        // Đang làm động tác: đứng yên, không thao tác gì được.
        if (move.IsLocked) { prompt = null; return; }

        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 0.2f;
            ScanNearby();
        }

        if (isCarrying.Value)
        {
            if (nearChair && input.PickupDown)
            {
                if (isBotNow) ServerPerformPlaceOnChair();
                else PlaceOnChairServerRpc();
            }
            if (input.DropDown)
            {
                if (isBotNow) ServerPerformDrop();
                else DropServerRpc();
            }
            prompt = nearChair
                ? $"[{input.Keys.PickupKey}] Đặt lên ghế tên lửa   [{input.Keys.DropKey}] Thả xuống"
                : $"Mang người tới ghế tên lửa   [{input.Keys.DropKey}] Thả xuống";
            return;
        }

        prompt = nearDowned ? $"[{input.Keys.PickupKey}] Bế người đang gục" : null;
        if (nearDowned && input.PickupDown)
        {
            if (isBotNow) ServerPerformPickUp();
            else PickUpServerRpc();
        }
    }

    void ScanNearby()
    {
        nearDowned = false;
        nearChair = false;
        nearDownedTarget = null;
        nearChairTarget = null;

        foreach (SurvivorState st in SurvivorState.All)
        {
            if (st.Current == SurvivorHealth.Downed
                && FlatDistance(st.transform.position) <= pickupRange
                && CanReach(st.transform.position, st.transform))
            {
                nearDowned = true;
                nearDownedTarget = st;
                break;
            }
        }
        foreach (RocketChair c in RocketChair.All)
        {
            if (c.IsFree
                && FlatDistance(c.transform.position) <= chairRange
                && CanReach(c.transform.position, c.transform))
            {
                nearChair = true;
                nearChairTarget = c;
                break;
            }
        }
    }

    float FlatDistance(Vector3 p)
    {
        Vector3 d = p - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    /// <summary>Cùng tầng và không có tường chắn giữa Hunter và mục tiêu.</summary>
    bool CanReach(Vector3 targetPos, Transform targetRoot)
    {
        if (Mathf.Abs(targetPos.y - transform.position.y) > maxHeightDiff) return false;
        return LineOfSight.Clear(transform.position, targetPos, eyeHeight, blockMask, targetRoot);
    }

    [ServerRpc]
    void PickUpServerRpc()
    {
        ServerPerformPickUp();
    }

    void ServerPerformPickUp()
    {
        if (!attack.ServerCanAct || isCarrying.Value) return;

        SurvivorState best = null;
        float bestDist = pickupRange + 0.5f;
        foreach (SurvivorState st in SurvivorState.All)
        {
            if (st.Current != SurvivorHealth.Downed) continue;
            float d = FlatDistance(st.transform.position);
            if (d <= bestDist && CanReach(st.transform.position, st.transform)) { bestDist = d; best = st; }
        }

        if (best == null || !best.ServerPickUp(NetworkObject)) return;

        carried = best;
        isCarrying.Value = true;
        attack.ServerBeginAction(lockOnPickup);
        PlayActionClientRpc(ActionPickup);
    }

    [ServerRpc]
    void PlaceOnChairServerRpc()
    {
        ServerPerformPlaceOnChair();
    }

    void ServerPerformPlaceOnChair()
    {
        if (!attack.ServerCanAct || !isCarrying.Value || carried == null) return;

        RocketChair chair = null;
        float bestDist = chairRange + 0.5f;
        foreach (RocketChair c in RocketChair.All)
        {
            if (!c.IsFree) continue;
            float d = FlatDistance(c.transform.position);
            if (d <= bestDist && CanReach(c.transform.position, c.transform)) { bestDist = d; chair = c; }
        }

        if (chair == null || !chair.ServerPlace(carried)) return;

        carried = null;
        isCarrying.Value = false;
        attack.ServerBeginAction(lockOnPlace);
        PlayActionClientRpc(ActionPlace);
    }

    [ServerRpc]
    void DropServerRpc()
    {
        ServerPerformDrop();
    }

    void ServerPerformDrop()
    {
        if (!attack.ServerCanAct || !isCarrying.Value) return;

        if (carried != null)
        {
            // Có tường phía trước thì thả gần Hunter hơn, không đẩy Survivor vào / qua tường.
            Vector3 origin = transform.position + Vector3.up * eyeHeight;
            float free = LineOfSight.FreeDistance(origin, transform.forward, 1f + dropClearance, blockMask);
            float dist = Mathf.Clamp(free - dropClearance, 0f, 1f);
            Vector3 dropPos = transform.position + transform.forward * dist;
            dropPos.y = transform.position.y;
            carried.ServerDrop(dropPos, transform.eulerAngles.y);
        }

        carried = null;
        isCarrying.Value = false;
        attack.ServerBeginAction(lockOnDrop);
        PlayActionClientRpc(ActionDrop);
    }

    [ClientRpc]
    void PlayActionClientRpc(byte action)
    {
        // Bế dùng pickup_survivor; thả và đặt lên ghế dùng drop_survivor.
        if (animator != null)
        {
            if (action == ActionPickup && hasPickup) animator.SetTrigger(PickupParam);
            else if (action != ActionPickup && hasDrop) animator.SetTrigger(DropParam);
        }

        if (IsOwner)
            move.LockFor(action == ActionPickup ? lockOnPickup : action == ActionDrop ? lockOnDrop : lockOnPlace);
    }
}
