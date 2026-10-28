using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Services.Authentication;
using UnityEngine;

/// <summary>
/// Gang vao ca prefab Survivor VA Hunter.
/// Khi spawn, owner gui PlayerId (UUID ben vung tu Unity Auth) len server.
/// Server dung de: (1) theo doi clientId - playerId, (2) nhan dien va khoi phuc state khi reconnect.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class PlayerIdentity : NetworkBehaviour
{
    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;

        // PlayerId tu Unity Auth (WAN/Relay). Fallback ve clientId cho LAN khong dung Auth.
        string pid = (AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn)
            ? AuthenticationService.Instance.PlayerId
            : "local_" + OwnerClientId.ToString();

        RegisterServerRpc(pid);
    }

    [ServerRpc]
    void RegisterServerRpc(string playerId, ServerRpcParams p = default)
    {
        ulong clientId = p.Receive.SenderClientId;
        ReconnectManager rm = ReconnectManager.Instance;
        if (rm == null) return;

        // TryConsume tra ve true neu day la reconnect co state da luu
        if (rm.TryConsume(clientId, playerId, out ReconnectManager.SavedState state))
        {
            // Anh xa lai role / character cho clientId moi
            RoleAssignments.Set(clientId, state.role);
            CharacterAssignments.Set(clientId, state.characterIndex);

            // Khoi phuc vi tri: teleport NetworkTransform toi vi tri da luu
            var nt = GetComponent<NetworkTransform>();
            Quaternion rot = Quaternion.Euler(0f, state.yaw, 0f);
            if (nt != null)
                nt.Teleport(state.position, rot, transform.localScale);
            else
                transform.SetPositionAndRotation(state.position, rot);

            // Khoi phuc mau Survivor (Hunter khong co SurvivorState)
            if (state.role == Role.Survivor)
            {
                var ss = GetComponent<SurvivorState>();
                if (ss != null && state.health != SurvivorHealth.Healthy)
                    ss.ServerSet(state.health);
            }

            // Thong bao MatchManager: reconnect thanh cong (xoa HunterReconnecting neu can)
            MatchManager.Instance?.OnPlayerReconnected(clientId, state.role);

            Debug.Log($"[Reconnect] State da duoc khoi phuc cho client {clientId} (vai {state.role}, health {state.health}).");
        }
        else
        {
            // Lan dau vao: chi dang ky clientId - playerId de theo doi
            rm.Register(clientId, playerId);
        }
    }
}
