using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gắn vào CẢ prefab Survivor VÀ Hunter.
/// - Server ghi tên hiển thị lúc spawn (lấy từ NameAssignments), mọi máy đọc qua PlayerName.
/// - Chủ nhân báo "tôi đã sẵn sàng" lên server; nếu là người vừa nối lại thì server khôi phục vị trí và máu.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class PlayerIdentity : NetworkBehaviour
{
    /// <summary>Tên hiển thị. Server ghi, mọi máy đọc (kể cả người vào sau hoặc nối lại).</summary>
    public readonly NetworkVariable<FixedString64Bytes> PlayerName = new NetworkVariable<FixedString64Bytes>();
    public string DisplayName => PlayerName.Value.ToString();

    public override void OnNetworkSpawn()
    {
        if (IsServer) PlayerName.Value = new FixedString64Bytes(NameAssignments.Get(OwnerClientId));
        if (IsOwner) RequestRestoreServerRpc();
    }

    [ServerRpc]
    void RequestRestoreServerRpc(ServerRpcParams p = default)
    {
        ulong clientId = p.Receive.SenderClientId;
        ReconnectManager rm = ReconnectManager.Instance;
        if (rm == null || !rm.TakeReserved(clientId, out ReconnectManager.SavedState state)) return;   // lần vào đầu tiên: không có gì để khôi phục

        bool stateApplied = false;        // đã tự đặt trạng thái cho Survivor mới (bế tiếp)
        bool chairTransferred = false;    // đã chuyển chỗ ngồi ghế sang clientId mới

        // Lấy vị trí và máu mới nhất từ bot đang thay thế, sau đó despawn bot
        if (BotController.ActiveBots.TryGetValue(state.oldClientId, out BotController bot) && bot != null)
        {
            state.hasPosition = true;
            state.position = bot.transform.position;
            state.yaw = bot.transform.eulerAngles.y;
            if (bot.Survivor != null) state.health = bot.Survivor.Current;

            // Bàn giao TRƯỚC khi despawn object cũ, vì Hunter và ghế vẫn đang giữ tham chiếu tới object đó.
            SurvivorState newSs = GetComponent<SurvivorState>();
            if (state.role == Role.Survivor && newSs != null && bot.Survivor != null)
            {
                if (state.health == SurvivorHealth.Carried)
                {
                    // Hunter đang bế người này: giao Survivor mới cho Hunter bế tiếp.
                    if (bot.Survivor.Carrier.Value.TryGet(out NetworkObject hunterNo))
                    {
                        HunterCarry hc = hunterNo.GetComponent<HunterCarry>();
                        if (hc != null && hc.ServerAdoptCarried(bot.Survivor, newSs))
                        {
                            newSs.ServerRestoreCarried(hunterNo);
                            stateApplied = true;
                        }
                    }
                }
                else if (state.health == SurvivorHealth.OnChair)
                {
                    foreach (RocketChair chair in RocketChair.All)
                        if (chair != null && chair.ServerTransferOccupant(state.oldClientId, clientId)) { chairTransferred = true; break; }
                }
            }

            bot.Release();
            if (bot.NetObj != null && bot.NetObj.IsSpawned)
                bot.NetObj.Despawn();
            Debug.Log($"[Reconnect] Despawn bot thay thế của client {state.oldClientId}");
        }

        // 1) Vị trí: NetworkTransform do chủ nhân điều khiển, nên chỉ chủ nhân dịch chuyển được -> gửi riêng cho họ.
        if (state.hasPosition)
        {
            RestorePositionClientRpc(state.position, state.yaw, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            });
        }

        // 2) Máu (Survivor)
        if (state.role == Role.Survivor)
        {
            SurvivorState ss = GetComponent<SurvivorState>();
            if (ss != null && !stateApplied)
            {
                SurvivorHealth h = state.health;
                // Bị bế / ngồi ghế mà không bàn giao được (Hunter đã thả, ghế đã trống...): hạ về trạng thái an toàn,
                // tránh kẹt khóa di chuyển mà không có Hunter / ghế nào giữ.
                if (h == SurvivorHealth.Carried) h = SurvivorHealth.Downed;
                else if (h == SurvivorHealth.OnChair && !chairTransferred) h = SurvivorHealth.Injured;
                if (h != SurvivorHealth.Healthy) ss.ServerSet(h);
            }
        }

        if (MatchManager.Instance != null) MatchManager.Instance.OnPlayerReconnected(clientId, state.role, state.oldClientId);
        Debug.Log("[Reconnect] Đã khôi phục client " + clientId + " (vai " + state.role + ", máu " + state.health + ").");
    }

    [ClientRpc]
    void RestorePositionClientRpc(Vector3 pos, float yaw, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        NetworkPlayerMovement move = GetComponent<NetworkPlayerMovement>();
        if (move != null) move.SnapTo(pos, yaw);
    }
}
