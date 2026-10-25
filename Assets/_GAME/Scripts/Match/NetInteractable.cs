using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Lớp cha cho mọi thứ Survivor nhấn F để làm: máy mã hóa, mở cổng, nhảy hầm.
/// Nhấn F một lần là thanh tiến độ tự chạy; đi xa / di chuyển / nhấn F lần nữa thì buông (tiến độ được giữ).
/// Riêng thứ nào đặt IsInstant = true (nhảy hầm) thì nhấn F là xong ngay, không có thanh tiến độ.
/// Client chỉ gửi "bắt đầu / dừng"; SERVER kiểm tra vai, trạng thái, khoảng cách rồi mới cộng tiến độ.
/// </summary>
public abstract class NetInteractable : NetworkBehaviour
{
    [SerializeField] protected float duration = 60f;   // giây nếu 1 người làm; nhiều người thì nhanh hơn
    [SerializeField] float range = 2.5f;
    [SerializeField] protected Renderer visual;                   // để trống thì tự tìm Renderer

    public readonly NetworkVariable<float> Progress = new NetworkVariable<float>(0f);
    public readonly NetworkVariable<bool> Done = new NetworkVariable<bool>(false);

    readonly HashSet<ulong> users = new HashSet<ulong>();
    readonly List<ulong> toRemove = new List<ulong>();
    /// <summary>Những người đang làm lúc hoàn thành (dùng trong OnCompleted).</summary>
    protected readonly List<ulong> finishers = new List<ulong>();
    float serverProgress;

    /// <summary>Mọi NetInteractable đang spawn, để khỏi phải FindObjectsByType mỗi lần quét.</summary>
    public static readonly List<NetInteractable> All = new List<NetInteractable>();

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static MaterialPropertyBlock colorBlock;
    Color lastColor;
    bool hasColor;

    public float Range => range;
    public abstract string PromptText { get; }
    public virtual bool IsAvailable => !Done.Value;
    /// <summary>true = nhấn F là xong ngay, không có thanh tiến độ (ví dụ nhảy hầm).</summary>
    public virtual bool IsInstant => false;
    /// <summary>Server hỏi trước khi cho userId làm (ví dụ chặn tự hồi máu cho chính mình). Mặc định cho phép.</summary>
    public virtual bool CanUse(ulong userId) => true;
    /// <summary>Server: userId đang làm thứ này không? Terror Shock dùng để biết Survivor đang hồi máu hoặc cứu người.</summary>
    public bool IsUsedBy(ulong clientId) => users.Contains(clientId);
    /// <summary>Người đang làm thứ này bị Hunter đánh trúng thì Gục ngay (Terror Shock). Lớp con bật nếu cần.</summary>
    public virtual bool TerrorShockOnUse => false;
    protected virtual void OnCompleted() { }
    /// <summary>Server: có người vừa bắt đầu làm. Lớp con ghi đè nếu cần (ghế tên lửa: khóa người cứu).</summary>
    protected virtual void OnUserAdded(ulong id) { }
    /// <summary>Server: có người vừa buông (đi xa, nhấn F lần nữa, bị đánh, rớt mạng...). Không gọi khi làm xong.</summary>
    protected virtual void OnUserRemoved(ulong id) { }
    protected virtual Color GetColor() => Color.Lerp(Color.red, Color.green, Progress.Value);

    public override void OnNetworkSpawn() { All.Add(this); }
    public override void OnNetworkDespawn() { All.Remove(this); }

    /// <summary>Khoảng cách trên mặt phẳng ngang (bỏ qua chênh lệch độ cao).</summary>
    public float FlatDistance(Vector3 p)
    {
        Vector3 d = p - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    [ServerRpc(RequireOwnership = false)]
    public void StartUseServerRpc(ServerRpcParams p = default)
    {
        if (MatchManager.Ended) return;
        ulong id = p.Receive.SenderClientId;
        if (!IsAvailable || !CanUse(id)) return;
        if (!TryGetSurvivor(id, out Transform t)) return;
        if (FlatDistance(t.position) > range + 0.5f) return;   // dung sai cho độ trễ
        if (IsInstant) { CompleteInstantly(id); return; }
        if (users.Add(id)) OnUserAdded(id);
    }

    /// <summary>
    /// Đưa tiến độ về 0 để dùng lại (ghế tên lửa, hồi máu). keepFinishers = true: những người vừa làm xong vẫn tiếp tục làm.
    /// Gọi trong OnCompleted.
    /// </summary>
    protected void ServerResetProgress(bool keepFinishers = false)
    {
        serverProgress = 0f;
        Progress.Value = 0f;
        Done.Value = false;
        if (keepFinishers) foreach (ulong id in finishers) users.Add(id);
    }

    /// <summary>Hoàn thành ngay trên server cho thứ IsInstant (không qua thanh tiến độ).</summary>
    void CompleteInstantly(ulong id)
    {
        if (Done.Value) return;
        serverProgress = 1f;
        Progress.Value = 1f;
        Done.Value = true;
        finishers.Clear();
        finishers.Add(id);
        OnCompleted();
    }

    [ServerRpc(RequireOwnership = false)]
    public void StopUseServerRpc(ServerRpcParams p = default)
    {
        if (users.Remove(p.Receive.SenderClientId)) OnUserRemoved(p.Receive.SenderClientId);
    }

    /// <summary>
    /// Lấy Survivor của clientId nếu hợp lệ. Mặc định phải còn hành động được (Khỏe/Bị thương).
    /// allowDowned = true: cho cả người đang Gục (dùng khi kiểm tra thoát qua cổng, vì người gục vẫn bò ra cổng được).
    /// </summary>
    protected static bool TryGetSurvivor(ulong clientId, out Transform t, bool allowDowned = false)
    {
        t = null;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.ConnectedClients.TryGetValue(clientId, out var c) || c.PlayerObject == null) return false;

        PlayerRoleInfo info = c.PlayerObject.GetComponent<PlayerRoleInfo>();
        if (info == null || info.CurrentRole != Role.Survivor) return false;

        SurvivorState st = c.PlayerObject.GetComponent<SurvivorState>();
        if (st != null)
        {
            bool ok = st.IsActive || (allowDowned && st.Current == SurvivorHealth.Downed);
            if (!ok) return false;
        }

        t = c.PlayerObject.transform;
        return true;
    }

   

    protected virtual void Update()
    {
        

        if (!IsServer || Done.Value || users.Count == 0) return;

        toRemove.Clear();
        foreach (ulong id in users)
        {
            if (!IsAvailable || !CanUse(id) || !TryGetSurvivor(id, out Transform t) || FlatDistance(t.position) > range + 0.5f)
                toRemove.Add(id);   // đi xa / rớt mạng / bị đánh / hết hiệu lực -> buông
        }
        foreach (ulong id in toRemove) { if (users.Remove(id)) OnUserRemoved(id); }
        if (users.Count == 0) return;

        serverProgress = Mathf.Min(1f, serverProgress + users.Count * Time.deltaTime / duration);
        if (serverProgress - Progress.Value >= 0.01f || serverProgress >= 1f)
            Progress.Value = serverProgress;   // ghi theo bước 1% để đỡ tốn băng thông

        if (serverProgress >= 1f)
        {
            Done.Value = true;
            finishers.Clear();
            finishers.AddRange(users);
            users.Clear();
            OnCompleted();
        }
    }
}