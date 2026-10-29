using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gắn vào GameObject NetworkManager (không bị hủy khi đổi scene). Quản lý reconnect phía SERVER.
///   Register     : ConnectionGate ghi clientId -> playerId ngay khi duyệt kết nối.
///   Snapshot     : mỗi 0,5 giây lưu vị trí và máu của mọi người chơi, nên không phụ thuộc thứ tự callback của NGO.
///   SaveAndHold  : khi một người rớt mạng, giữ state của họ trong Window giây.
///   TryReserve   : người đó nối lại giữa trận: gán vai/nhân vật cho clientId MỚI trước khi spawn.
///   TakeReserved : sau khi nhân vật được spawn, PlayerIdentity lấy state ra để khôi phục.
/// </summary>
public class ReconnectManager : MonoBehaviour
{
    public static ReconnectManager Instance { get; private set; }

    /// <summary>Cửa sổ reconnect (giây) kể từ lúc server biết người đó rớt.</summary>
    public const float Window = 30f;
    /// <summary>Thời gian (giây) cho người vừa được duyệt tải scene và spawn xong.</summary>
    public const float SpawnGrace = 20f;
    const float SnapshotInterval = 0.5f;

    public struct SavedState
    {
        public string playerId;
        public ulong oldClientId;
        public Role role;
        public int characterIndex;
        public bool hasPosition;
        public Vector3 position;
        public float yaw;
        public SurvivorHealth health;
        public float deadline;   // Time.time khi hết hạn
    }

    struct Snap
    {
        public Vector3 pos;
        public float yaw;
        public SurvivorHealth health;
    }

    readonly Dictionary<string, SavedState> pending = new Dictionary<string, SavedState>();   // playerId -> state đang chờ
    readonly Dictionary<ulong, string> idMap = new Dictionary<ulong, string>();               // clientId đang kết nối -> playerId
    readonly Dictionary<ulong, string> reservedIds = new Dictionary<ulong, string>();         // clientId mới đã được giữ chỗ -> playerId
    readonly Dictionary<ulong, Snap> snaps = new Dictionary<ulong, Snap>();
    readonly List<string> expiredBuffer = new List<string>();
    float snapTimer;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ================== Đăng ký & tra cứu ==================

    /// <summary>SERVER: ghi nhận clientId này thuộc về playerId nào (gọi từ ConnectionGate).</summary>
    public void Register(ulong clientId, string playerId)
    {
        idMap[clientId] = playerId;
    }

    /// <summary>SERVER: playerId của clientId (null nếu chưa đăng ký).</summary>
    public string GetPlayerId(ulong clientId)
    {
        return idMap.TryGetValue(clientId, out string p) ? p : null;
    }

    /// <summary>SERVER: clientId (cũ) này có đang chờ reconnect không? RocketChair dùng để tạm giữ ghế.</summary>
    public bool IsInReconnectPending(ulong oldClientId)
    {
        foreach (var kv in pending)
            if (kv.Value.oldClientId == oldClientId && Time.time < kv.Value.deadline)
                return true;
        return false;
    }

    // ================== Lưu khi rớt mạng ==================

    /// <summary>
    /// SERVER: gọi từ MatchManager.OnClientDisconnected. Lấy state từ playerObj nếu còn, nếu không thì dùng snapshot gần nhất.
    /// Trả về true nếu đã giữ chỗ cho người này.
    /// </summary>
    public bool SaveAndHold(ulong clientId, NetworkObject playerObj)
    {
        if (!idMap.TryGetValue(clientId, out string pid))
        {
            Debug.Log("[Reconnect] Không có playerId cho client " + clientId + ", không giữ chỗ.");
            return false;
        }

        bool hasPos = false;
        Vector3 pos = Vector3.zero;
        float yaw = 0f;
        SurvivorHealth hp = SurvivorHealth.Healthy;

        if (playerObj != null)
        {
            hasPos = true;
            pos = playerObj.transform.position;
            yaw = playerObj.transform.eulerAngles.y;
            SurvivorState ss = playerObj.GetComponent<SurvivorState>();
            if (ss != null) hp = ss.Current;
        }
        else if (snaps.TryGetValue(clientId, out Snap sn))
        {
            hasPos = true;
            pos = sn.pos;
            yaw = sn.yaw;
            hp = sn.health;
        }

        pending[pid] = new SavedState
        {
            playerId = pid,
            oldClientId = clientId,
            role = RoleAssignments.Get(clientId),
            characterIndex = CharacterAssignments.Get(clientId),
            hasPosition = hasPos,
            position = pos,
            yaw = yaw,
            health = hp,
            deadline = Time.time + Window
        };
        idMap.Remove(clientId);
        snaps.Remove(clientId);
        Debug.Log("[Reconnect] Đã giữ chỗ " + pid + " (vai " + pending[pid].role + "), hạn " + Window + "s.");
        return true;
    }

    // ================== Nối lại ==================

    /// <summary>
    /// SERVER (gọi từ ConnectionGate lúc duyệt kết nối giữa trận): nếu playerId đang chờ và còn hạn thì gán lại vai và nhân vật
    /// cho clientId MỚI, để MatchSpawner spawn đúng prefab. Chưa xóa state: PlayerIdentity sẽ lấy sau khi spawn.
    /// </summary>
    public bool TryReserve(ulong newClientId, string playerId)
    {
        if (string.IsNullOrEmpty(playerId)) return false;
        if (!pending.TryGetValue(playerId, out SavedState s)) return false;
        if (Time.time > s.deadline) return false;   // hết hạn: để Update() gọi FinalizeDisconnect

        RoleAssignments.Set(newClientId, s.role);
        CharacterAssignments.Set(newClientId, s.characterIndex);
        idMap[newClientId] = playerId;
        reservedIds[newClientId] = playerId;

        // Cho người này thêm thời gian tải scene và spawn (nếu không spawn kịp thì Update() sẽ tính là rớt hẳn).
        float grace = Time.time + SpawnGrace;
        if (grace > s.deadline) s.deadline = grace;
        pending[playerId] = s;
        return true;
    }

    /// <summary>SERVER: PlayerIdentity gọi sau khi nhân vật mới đã spawn xong ở phía chủ nhân.</summary>
    public bool TakeReserved(ulong clientId, out SavedState state)
    {
        state = default;
        if (!reservedIds.TryGetValue(clientId, out string pid)) return false;
        reservedIds.Remove(clientId);
        if (!pending.TryGetValue(pid, out state)) return false;
        pending.Remove(pid);
        return true;
    }

    /// <summary>SERVER: xóa mọi dữ liệu của người này (đã thoát/bị loại bình thường, không cần reconnect nữa).</summary>
    public void Unregister(ulong clientId)
    {
        snaps.Remove(clientId);
        reservedIds.Remove(clientId);
        if (idMap.TryGetValue(clientId, out string pid))
        {
            idMap.Remove(clientId);
            pending.Remove(pid);
        }
    }

    /// <summary>SERVER: xóa toàn bộ khi trận kết thúc hoặc về lobby.</summary>
    public void Clear()
    {
        pending.Clear();
        idMap.Clear();
        reservedIds.Clear();
        snaps.Clear();
    }

    // ================== Định kỳ ==================

    void Update()
    {
        if (Instance != this) return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;

        snapTimer += Time.unscaledDeltaTime;
        if (snapTimer >= SnapshotInterval)
        {
            snapTimer = 0f;
            TakeSnapshots(nm);
        }

        expiredBuffer.Clear();
        foreach (var kv in pending)
            if (Time.time >= kv.Value.deadline) expiredBuffer.Add(kv.Key);

        foreach (string pid in expiredBuffer)
        {
            SavedState s = pending[pid];
            pending.Remove(pid);
            RemoveReservations(pid);
            Debug.Log("[Reconnect] " + pid + " hết hạn, tính là rớt hẳn.");
            if (MatchManager.Instance != null) MatchManager.Instance.FinalizeDisconnect(s);
        }
    }

    void TakeSnapshots(NetworkManager nm)
    {
        foreach (var kv in nm.ConnectedClients)
        {
            NetworkObject po = kv.Value.PlayerObject;
            if (po == null) continue;
            SurvivorState ss = po.GetComponent<SurvivorState>();
            snaps[kv.Key] = new Snap
            {
                pos = po.transform.position,
                yaw = po.transform.eulerAngles.y,
                health = ss != null ? ss.Current : SurvivorHealth.Healthy
            };
        }
    }

    void RemoveReservations(string pid)
    {
        List<ulong> drop = null;
        foreach (var kv in reservedIds)
            if (kv.Value == pid)
            {
                if (drop == null) drop = new List<ulong>();
                drop.Add(kv.Key);
            }
        if (drop != null) foreach (ulong id in drop) reservedIds.Remove(id);
    }
}
