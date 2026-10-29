using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gắn vào GameObject NetworkManager. Bật ConnectionApproval và quyết định có cho client vào hay không
/// TRƯỚC khi client được tính là đã kết nối:
///   - sai phiên bản game            -> từ chối
///   - phòng chờ đã đủ người          -> từ chối
///   - đang giữa trận                 -> chỉ nhận người đang chờ reconnect (đúng PlayerId); người lạ bị từ chối
///   - trận đã kết thúc               -> từ chối
/// Với người nối lại, vai và nhân vật được gán cho clientId MỚI ngay tại đây, nên MatchSpawner spawn đúng prefab.
/// Lý do từ chối hiện ở client qua NetworkManager.DisconnectReason.
/// Thêm: ghi tên hiển thị của từng clientId vào NameAssignments ngay lúc duyệt kết nối.
/// </summary>
[DefaultExecutionOrder(-500)]
public class ConnectionGate : MonoBehaviour
{
    [SerializeField] bool checkVersion = true;

    NetworkManager nm;

    void Awake()
    {
        nm = GetComponent<NetworkManager>();
        if (nm == null)
        {
            Debug.LogError("ConnectionGate: hãy gắn script này vào object NetworkManager.");
            enabled = false;
            return;
        }
        if (NetworkManager.Singleton != null && NetworkManager.Singleton != nm) return;

        nm.NetworkConfig.ConnectionApproval = true;
        nm.ConnectionApprovalCallback = Approve;
    }

    void Approve(NetworkManager.ConnectionApprovalRequest req, NetworkManager.ConnectionApprovalResponse res)
    {
        res.CreatePlayerObject = false;
        res.Pending = false;

        // Host
        if (req.ClientNetworkId == NetworkManager.ServerClientId)
        {
            res.Approved = true;
            NameAssignments.Set(req.ClientNetworkId, PlayerProfile.Name);          // <-- THÊM
            if (ReconnectManager.Instance != null)
                ReconnectManager.Instance.Register(req.ClientNetworkId, LocalIdentity.PlayerId);
            return;
        }

        if (!LocalIdentity.TryParse(req.Payload, out string version, out string playerId, out string playerName))
        {
            Deny(res, "Thiếu thông tin định danh (bản game quá cũ?)");
            return;
        }
        if (checkVersion && version != Application.version)
        {
            Deny(res, "Sai phiên bản game: bạn " + version + ", host " + Application.version);
            return;
        }

        MatchManager match = MatchManager.Instance;
        if (match != null)
        {
            if (match.IsEnded) { Deny(res, "Trận đã kết thúc"); return; }

            ReconnectManager rm = ReconnectManager.Instance;
            if (rm != null && rm.TryReserve(req.ClientNetworkId, playerId))
            {
                res.Approved = true;
                NameAssignments.Set(req.ClientNetworkId, playerName);              // <-- THÊM (clientId MỚI)
                Debug.Log("[Gate] Cho " + playerId + " (" + playerName + ") nối lại (clientId mới " + req.ClientNetworkId + ")");
                return;
            }
            Deny(res, "Trận đang diễn ra");
            return;
        }

        // Phòng chờ
        if (NetworkManager.Singleton.ConnectedClientsIds.Count >= LobbyManager.MaxPlayers)
        {
            Deny(res, "Phòng đã đủ người");
            return;
        }
        res.Approved = true;
        NameAssignments.Set(req.ClientNetworkId, playerName);                      // <-- THÊM
        if (ReconnectManager.Instance != null)
            ReconnectManager.Instance.Register(req.ClientNetworkId, playerId);
        Debug.Log("[Gate] Chấp nhận client " + req.ClientNetworkId + " (" + playerId + ", tên \"" + playerName + "\")");
    }

    static void Deny(NetworkManager.ConnectionApprovalResponse res, string reason)
    {
        res.Approved = false;
        res.Reason = reason;
        Debug.LogWarning("[Gate] Từ chối kết nối: " + reason);
    }
}