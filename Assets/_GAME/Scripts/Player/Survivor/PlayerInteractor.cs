using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gắn vào prefab Survivor (và Hunter, script tự bỏ qua nếu không phải Survivor).
/// Chỉ chạy trên máy của chủ nhân nhân vật: nhấn F gần máy/cổng/hầm để bắt đầu, thanh tiến độ tự chạy;
/// di chuyển hoặc nhấn F lần nữa để buông. Không vẽ giao diện: InteractUI (Canvas) đọc Local.Target.
/// Hỗ trợ bot: khi bot active trên server, gọi ServerStartUse/ServerStopUse trực tiếp thay vì RPC.
/// </summary>
[RequireComponent(typeof(PlayerRoleInfo))]
public class PlayerInteractor : NetworkBehaviour
{
    /// <summary>PlayerInteractor của người chơi trên máy này (null nếu chưa spawn).</summary>
    public static PlayerInteractor Local { get; private set; }


    PlayerRoleInfo role;
    SurvivorState state;
    NetInteractable nearest, current;
    float nextScan;
    BotController botCtrl;
    static readonly int DecodingParam = Animator.StringToHash("isDecoding");
    readonly NetworkVariable<bool> isDecoding = new NetworkVariable<bool>(
        false, NetworkVariableBase.DefaultReadPerm, NetworkVariableWritePermission.Owner);
    Animator animator;
    bool hasDecodingParam;

    /// <summary>Thứ đang làm, hoặc thứ gần nhất có thể làm (null nếu không có).</summary>
    public NetInteractable Target => current != null ? current : nearest;
    /// <summary>true khi đang giữ máy/cổng/hầm (thanh tiến độ đang chạy).</summary>
    public bool IsWorking => current != null;

    CharacterInput input;

    void Awake()
    {
        role = GetComponent<PlayerRoleInfo>();
        input = CharacterInput.Of(gameObject);
        state = GetComponent<SurvivorState>();   // có thể null nếu chưa gắn
        botCtrl = GetComponent<BotController>();
        animator = GetComponentInChildren<Animator>();
        if (animator != null)
            foreach (AnimatorControllerParameter p in animator.parameters)
                if (p.nameHash == DecodingParam) { hasDecodingParam = true; break; }
    }

    public override void OnNetworkSpawn()
    {
        // Bật cho owner; server cũng cần enabled để bot chạy sau ChangeOwnership
        enabled = IsOwner || IsServer;
        if (IsOwner) Local = this;
        isDecoding.OnValueChanged += OnDecodingChanged;   // mới
        ApplyDecoding(isDecoding.Value);
    }

    public override void OnNetworkDespawn()
    {
        if (Local == this) Local = null;
        isDecoding.OnValueChanged -= OnDecodingChanged;
    }

    /// <summary>Bot đang hoạt động trên server?</summary>
    bool IsBotActive => IsServer && botCtrl != null && botCtrl.State != BotController.BotState.Off;

    /// <summary>ClientId dùng cho CanUse: OriginalClientId nếu bot, OwnerClientId nếu người chơi.</summary>
    ulong EffectiveClientId => IsBotActive ? botCtrl.OriginalClientId : OwnerClientId;

    void Update()
    {
        if (!IsSpawned) return;

        // Chỉ chạy nếu là owner thực sự HOẶC server-bot
        if (!IsOwner && !IsBotActive) return;

        bool canAct = role.CurrentRole == Role.Survivor && (state == null || state.IsActive);
        if (!canAct)
        {
            if (current != null) { StopCurrent(); }
            nearest = null;
            return;
        }

        bool moving = input.Move.sqrMagnitude > 0.01f;

        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 0.2f;
            nearest = FindNearest();
        }

        if (current != null)
        {
            bool tooFar = current.FlatDistance(transform.position) > current.Range + 0.5f;
            if (moving || tooFar || current.Done.Value || !current.IsAvailable || !current.CanUse(EffectiveClientId) || input.InteractDown)
            {
                NetStatsLogger.Mark("interact_stop");
                StopCurrent();
            }
            return;
        }

        if (nearest != null && !moving && input.InteractDown)
        {
            NetInteractable target = nearest;
            NetStatsLogger.Mark("interact_start_" + target.name);

            // Bot trên server → gọi trực tiếp, không qua RPC
            if (IsBotActive)
                target.ServerStartUse(botCtrl.OriginalClientId, transform);
            else
                target.StartUseServerRpc();

            if (target.IsInstant) nearest = null;
            else { current = target; SetDecoding(target is CipherMachine); }
        }
    }

    void StopCurrent()
    {
        if (current == null) return;

        if (IsBotActive)
            current.ServerStopUse(botCtrl.OriginalClientId);
        else
            current.StopUseServerRpc();

        SetDecoding(false);
        current = null;
    }

    NetInteractable FindNearest()
    {
        ulong checkId = EffectiveClientId;
        NetInteractable best = null;
        float bestDist = float.MaxValue;
        foreach (NetInteractable it in NetInteractable.All)
        {
            if (!it.IsSpawned || !it.IsAvailable || !it.CanUse(checkId)) continue;
            float d = it.FlatDistance(transform.position);
            if (d <= it.Range && d < bestDist) { bestDist = d; best = it; }
        }
        return best;
    }
    void SetDecoding(bool value)
    {
        if (IsOwner) isDecoding.Value = value;   // chỉ chủ nhân được ghi
    }

    void OnDecodingChanged(bool previous, bool value) => ApplyDecoding(value);

    void ApplyDecoding(bool value)
    {
        if (animator != null && hasDecodingParam) animator.SetBool(DecodingParam, value);
    }
}
