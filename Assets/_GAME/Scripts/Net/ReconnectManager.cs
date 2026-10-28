using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gang vao GameObject NetworkManager (khong bi huy khi doi scene).
/// Quan ly reconnect SERVER-SIDE: luu trang thai player khi rot mang,
/// khoi phuc khi client ket noi lai trong 30 giay.
/// </summary>
public class ReconnectManager : MonoBehaviour
{
    public static ReconnectManager Instance { get; private set; }

    /// <summary>Cua so reconnect (giay) cho moi vai.</summary>
    public const float Window = 30f;

    public struct SavedState
    {
        public string  playerId;
        public ulong   oldClientId;
        public Role    role;
        public int     characterIndex;
        public Vector3 position;
        public float   yaw;
        public SurvivorHealth health;
        public float   deadline;   // Time.time khi het han
    }

    // playerId -> state dang cho reconnect
    readonly Dictionary<string, SavedState> pending = new Dictionary<string, SavedState>();
    // clientId -> playerId (xoa khi disconnect hoac finalize)
    readonly Dictionary<ulong, string>      idMap   = new Dictionary<ulong, string>();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }
    void OnDestroy() { if (Instance == this) Instance = null; }

    // ================== Dang ky & tra cuu ==================

    /// <summary>SERVER: PlayerIdentity goi khi spawn lan dau de dang ky clientId - playerId.</summary>
    public void Register(ulong clientId, string playerId)
    {
        idMap[clientId] = playerId;
        Debug.Log($"[Reconnect] Dang ky client {clientId} -> {playerId}");
    }

    /// <summary>SERVER: tra ve playerId cua clientId (null neu chua dang ky).</summary>
    public string GetPlayerId(ulong clientId) =>
        idMap.TryGetValue(clientId, out string p) ? p : null;

    /// <summary>
    /// SERVER: Kiem tra xem clientId (cu) co dang cho reconnect khong.
    /// RocketChair va code khac goi de biet co nen giu chair hay khong.
    /// </summary>
    public bool IsInReconnectPending(ulong oldClientId)
    {
        foreach (var kv in pending)
            if (kv.Value.oldClientId == oldClientId && Time.time < kv.Value.deadline)
                return true;
        return false;
    }

    // ================== Luu va khoi phuc ==================

    /// <summary>
    /// SERVER: Luu trang thai player truoc khi NetworkObject bi huy (goi trong OnClientDisconnected).
    /// Tra ve true neu playerId da duoc dang ky va luu thanh cong.
    /// </summary>
    public bool SaveAndHold(ulong clientId, NetworkObject playerObj)
    {
        if (!idMap.TryGetValue(clientId, out string pid))
        {
            Debug.Log($"[Reconnect] Khong tim thay playerId cho client {clientId}, khong luu state.");
            return false;
        }

        var ss = playerObj != null ? playerObj.GetComponent<SurvivorState>() : null;

        pending[pid] = new SavedState
        {
            playerId       = pid,
            oldClientId    = clientId,
            role           = RoleAssignments.Get(clientId),
            characterIndex = CharacterAssignments.Get(clientId),
            position       = playerObj != null ? playerObj.transform.position : Vector3.zero,
            yaw            = playerObj != null ? playerObj.transform.eulerAngles.y : 0f,
            health         = ss != null ? ss.Current : SurvivorHealth.Healthy,
            deadline       = Time.time + Window
        };

        idMap.Remove(clientId);
        Debug.Log($"[Reconnect] Da luu state {pid} (vai {pending[pid].role}, pos {pending[pid].position}). Deadline trong {Window}s.");
        return true;
    }

    /// <summary>
    /// SERVER: Thu khoi phuc state khi player reconnect.
    /// Neu tim thay va con trong han -> xoa khoi pending, dang ky clientId moi, tra ve true.
    /// </summary>
    public bool TryConsume(ulong newClientId, string playerId, out SavedState state)
    {
        state = default;
        if (!pending.TryGetValue(playerId, out state)) return false;

        if (Time.time > state.deadline)
        {
            pending.Remove(playerId);
            Debug.Log($"[Reconnect] {playerId} reconnect nhung da het han ({Window}s).");
            return false;
        }

        pending.Remove(playerId);
        idMap[newClientId] = playerId;
        Debug.Log($"[Reconnect] {playerId} reconnect thanh cong! ClientId moi: {newClientId}");
        return true;
    }

    /// <summary>SERVER: Xoa dang ky khi player thoat/bi loai binh thuong (khong can reconnect).</summary>
    public void Unregister(ulong clientId)
    {
        if (idMap.TryGetValue(clientId, out string pid))
        {
            idMap.Remove(clientId);
            pending.Remove(pid);
        }
    }

    /// <summary>SERVER: Xoa toan bo khi tran ket thuc / ve lobby.</summary>
    public void Clear()
    {
        pending.Clear();
        idMap.Clear();
        Debug.Log("[Reconnect] Da xoa toan bo state.");
    }

    // ================== Timer ==================

    void Update()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        List<string> expired = null;
        foreach (var kv in pending)
        {
            if (Time.time >= kv.Value.deadline)
            {
                if (expired == null) expired = new List<string>();
                expired.Add(kv.Key);
            }
        }
        if (expired == null) return;

        foreach (string pid in expired)
        {
            SavedState s = pending[pid];
            pending.Remove(pid);
            Debug.Log($"[Reconnect] {pid} het {Window}s, finalize disconnect.");
            MatchManager.Instance?.FinalizeDisconnect(s);
        }
    }
}
