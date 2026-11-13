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
            if (ss != null && state.health != SurvivorHealth.Healthy) ss.ServerSet(state.health);
        }

        if (MatchManager.Instance != null) MatchManager.Instance.OnPlayerReconnected(clientId, state.role);
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