using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Tự dậy: Survivor đang GỤC giữ phím F, thanh tiến độ chạy, đủ thời gian thì Gục -> Bị thương (giống đồng đội đỡ dậy bằng HealInteractable).
/// Gắn vào prefab SURVIVOR (cùng object với SurvivorState).
///  - Owner giữ F: gửi "đang giữ / thôi giữ" lên server; trong lúc giữ nhân vật đứng yên (không bò).
///  - SERVER cộng tiến độ và quyết định khi nào xong (client không tự hồi máu được).
///  - Buông F thì tiến độ được GIỮ LẠI; mất khi trạng thái không còn là Gục (được đỡ dậy, bị bế...).
///  - Đồng đội đỡ dậy cùng lúc không ảnh hưởng nhau: ai xong trước thì Gục -> Bị thương, tiến độ tự về 0.
/// Bot không dùng (chỉ người chơi thật giữ F).
/// </summary>
[RequireComponent(typeof(SurvivorState))]
[RequireComponent(typeof(NetworkPlayerMovement))]
public class SelfRevive : NetworkBehaviour
{
    /// <summary>SelfRevive của người chơi trên máy này (null nếu chưa spawn). InteractUI đọc để hiện lời nhắc.</summary>
    public static SelfRevive Local { get; private set; }

    [Tooltip("Giữ F bao nhiêu giây thì tự dậy được (Gục -> Bị thương).")]
    [SerializeField] float duration = 12f;

    /// <summary>0..1, server ghi, mọi máy đọc.</summary>
    public readonly NetworkVariable<float> Progress = new NetworkVariable<float>(0f);

    SurvivorState state;
    NetworkPlayerMovement move;
    BotController botCtrl;
    CharacterInput input;

    bool serverHolding;   // server: owner đang giữ F
    float serverProgress;
    bool ownerHolding;    // owner: đã báo server là đang giữ

    bool IsBotActive => botCtrl != null && botCtrl.State != BotController.BotState.Off;

    /// <summary>Lời nhắc cho InteractUI (null nếu không phải lúc để tự dậy).</summary>
    public string Prompt
    {
        get
        {
            if (!IsOwner || state == null || state.Current != SurvivorHealth.Downed) return null;
            int pct = Mathf.RoundToInt(Mathf.Clamp01(Progress.Value) * 100f);
            return ownerHolding
                ? $"Đang tự dậy... {pct}%"
                : $"Giữ [{input.Keys.InteractKey}] để tự dậy ({pct}%)";
        }
    }

    void Awake()
    {
        state = GetComponent<SurvivorState>();
        move = GetComponent<NetworkPlayerMovement>();
        botCtrl = GetComponent<BotController>();
        input = CharacterInput.Of(gameObject);
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner) Local = this;
    }

    public override void OnNetworkDespawn()
    {
        if (Local == this) Local = null;
        StopHolding();
    }

    public override void OnLostOwnership()
    {
        // Người chơi rớt mạng / đổi chủ: thôi giữ, khỏi kẹt trạng thái "đang giữ" trên server.
        if (Local == this) Local = null;
        StopHolding();
    }

    public override void OnGainedOwnership()
    {
        if (IsOwner) Local = this;
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (IsOwner && !IsBotActive) OwnerUpdate();
        if (IsServer) ServerUpdate();
    }

    // ───────────── Owner ─────────────

    void OwnerUpdate()
    {
        bool downed = state.Current == SurvivorHealth.Downed && !MatchManager.Ended;
        bool wantHold = downed && Input.GetKey(input.Keys.InteractKey);

        if (wantHold == ownerHolding) return;
        ownerHolding = wantHold;
        move.SetLocked(this, wantHold);          // giữ F thì đứng yên, buông thì bò tiếp
        HoldServerRpc(wantHold);
    }

    void StopHolding()
    {
        if (ownerHolding && move != null) move.SetLocked(this, false);
        ownerHolding = false;
        serverHolding = false;
    }

    [ServerRpc]   // chỉ chủ nhân gọi được
    void HoldServerRpc(bool holding) => serverHolding = holding && state.Current == SurvivorHealth.Downed;

    // ───────────── Server ─────────────

    void ServerUpdate()
    {
        if (state.Current != SurvivorHealth.Downed || MatchManager.Ended)
        {
            // Không còn Gục (được đỡ, bị bế, lên ghế...): hủy giữ và đặt lại tiến độ.
            serverHolding = false;
            if (serverProgress > 0f) { serverProgress = 0f; Progress.Value = 0f; }
            return;
        }

        if (!serverHolding) return;

        serverProgress = Mathf.Min(1f, serverProgress + Time.deltaTime / Mathf.Max(0.1f, duration));
        if (serverProgress - Progress.Value >= 0.01f || serverProgress >= 1f)
            Progress.Value = serverProgress;   // ghi theo bước 1% cho đỡ tốn băng thông

        if (serverProgress >= 1f)
        {
            serverProgress = 0f;
            Progress.Value = 0f;
            serverHolding = false;
            state.ServerHeal();                // Gục -> Bị thương
            ReleaseClientRpc(new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
            });
        }
    }

    /// <summary>Chỉ máy chủ nhân: dậy xong thì mở khóa đứng yên để chạy được ngay.</summary>
    [ClientRpc]
    void ReleaseClientRpc(ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        ownerHolding = false;
        move.SetLocked(this, false);
    }
}
