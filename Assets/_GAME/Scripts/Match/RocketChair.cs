using UnityEngine.UI;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Ghế tên lửa. Gắn NetworkObject + script này vào RocketChair_1..N trong scene Match (đặt sẵn trong scene).
///  - Hunter đặt người đang Gục lên ghế (HunterCarry gọi ServerPlace); SERVER đếm ngược, hết giờ thì người đó bị loại.
///  - Survivor khác nhấn F gần ghế để cứu (thanh tiến độ tự chạy như giải mã); xong thì người trên ghế về Bị thương.
/// Kế thừa NetInteractable: "Progress" ở đây là tiến độ CỨU, "duration" là số giây cứu.
/// Luật cứu: mỗi ghế chỉ MỘT người cứu tại một thời điểm (đang có người cứu thì Survivor khác không cứu được);
/// một Survivor chỉ cứu một ghế cùng lúc (đang giữ ghế này thì không làm được gì khác);
/// tiến độ cứu ĐỘC LẬP, không lưu: buông ghế (đi xa, nhấn F lần nữa, bị đánh, rớt mạng) thì về 0 và lần cứu sau chạy lại từ đầu.
/// </summary>
public class RocketChair : NetInteractable
{
    [Header("Ghế")]
    [Tooltip("Điểm người bị trói ngồi (Empty con của ghế). Để trống = tâm ghế.")]
    [SerializeField] Transform seat;
    [Tooltip("Chỗ người được cứu đứng dậy (Empty con, đặt trước ghế khoảng 1.5m). Để trống = trước ghế 1.5m.")]
    [SerializeField] Transform releasePoint;
    [SerializeField] float countdownSeconds = 60f;
    [Tooltip("Tùy chọn: TextMeshPro 3D trên ghế để hiện số giây còn lại.")]
    [SerializeField] Text countdownLabel;

    public const ulong None = ulong.MaxValue;

    public readonly NetworkVariable<ulong> OccupantId = new NetworkVariable<ulong>(None);
    public readonly NetworkVariable<float> TimeLeft = new NetworkVariable<float>(0f);
    /// <summary>Ai đang cứu ghế này (None nếu chưa có ai). Mọi máy đọc để ẩn lời nhắc khi đã có người cứu.</summary>
    public readonly NetworkVariable<ulong> RescuerId = new NetworkVariable<ulong>(None);

    float serverTimeLeft;     // server
    float lastSentTimeLeft;   // server
    int shownSeconds = -1;    // mọi máy

    public static readonly System.Collections.Generic.List<RocketChair> All = new System.Collections.Generic.List<RocketChair>();

    public bool IsFree => OccupantId.Value == None;

    public override void OnNetworkSpawn() { base.OnNetworkSpawn(); All.Add(this); }
    public override void OnNetworkDespawn() { base.OnNetworkDespawn(); All.Remove(this); }

    public override string PromptText => "Cứu đồng đội khỏi ghế";
    public override bool TerrorShockOnUse => true;
    public override bool IsAvailable => IsSpawned && OccupantId.Value != None;


    /// <summary>Không tự cứu mình; và nếu đã có người đang cứu thì người khác không cứu được.</summary>
    public override bool CanUse(ulong userId) =>
        userId != OccupantId.Value && (RescuerId.Value == None || RescuerId.Value == userId);

    protected override void OnUserAdded(ulong id)
    {
        RescuerId.Value = id;
    }

    protected override void OnUserRemoved(ulong id)
    {
        if (RescuerId.Value == id) RescuerId.Value = None;
        ServerResetProgress();   // tiến độ cứu độc lập: buông là về 0
    }

    // ---------------- Server: đặt người lên ghế ----------------

    /// <summary>Hunter gọi khi đặt người đang bế lên ghế. Trả về true nếu thành công.</summary>
    public bool ServerPlace(SurvivorState victim)
    {
        if (!IsServer || !IsFree || victim == null) return false;

        Vector3 seatPos = seat != null ? seat.position : transform.position;
        float seatYaw = seat != null ? seat.eulerAngles.y : transform.eulerAngles.y;

        OccupantId.Value = victim.OwnerClientId;
        serverTimeLeft = countdownSeconds;
        lastSentTimeLeft = countdownSeconds;
        TimeLeft.Value = countdownSeconds;
        ServerResetProgress();                       // tiến độ cứu bắt đầu lại từ 0

        victim.ServerPutOnChair(seatPos, seatYaw);
        return true;
    }

    // ---------------- Server: được cứu ----------------

    protected override void OnCompleted()
    {
        SurvivorState v = GetOccupant();
        if (v != null && v.Current == SurvivorHealth.OnChair)
        {
            Vector3 pos = releasePoint != null ? releasePoint.position : transform.position + transform.forward * 1.5f;
            float yaw = releasePoint != null ? releasePoint.eulerAngles.y : transform.eulerAngles.y;
            v.ServerRescue(pos, yaw);
        }
        Vacate();
    }

    SurvivorState GetOccupant()
    {
        if (!IsServer || OccupantId.Value == None) return null;
        if (!NetworkManager.ConnectedClients.TryGetValue(OccupantId.Value, out var c) || c.PlayerObject == null) return null;
        return c.PlayerObject.GetComponent<SurvivorState>();
    }

    void Vacate()
    {
        OccupantId.Value = None;
        RescuerId.Value = None;
        serverTimeLeft = 0f;
        TimeLeft.Value = 0f;
        ServerResetProgress();
    }

    // ---------------- Cập nhật ----------------

    protected override void Update()
    {
        base.Update();      // tiến độ cứu + màu (UpdateVisual)
        UpdateLabel();      // mọi máy

        if (!IsServer || IsFree) return;

        SurvivorState v = GetOccupant();
        if (v == null || v.Current != SurvivorHealth.OnChair)
        {
            Vacate();       // người đó rớt mạng hoặc trạng thái bị đổi từ nơi khác
            return;
        }

        serverTimeLeft -= Time.deltaTime;
        if (serverTimeLeft <= 0f)
        {
            if (MatchManager.Instance != null) MatchManager.Instance.OnSurvivorEliminated(v.OwnerClientId);
            else v.ServerSet(SurvivorHealth.Eliminated);
            Vacate();
            return;
        }

        // Ghi biến mạng theo bước 0.5 giây để đỡ tốn băng thông.
        if (lastSentTimeLeft - serverTimeLeft >= 0.5f)
        {
            lastSentTimeLeft = serverTimeLeft;
            TimeLeft.Value = serverTimeLeft;
        }
    }

    void UpdateLabel()
    {
        if (countdownLabel == null) return;
        int s = IsFree ? 0 : Mathf.CeilToInt(TimeLeft.Value);
        if (s == shownSeconds) return;
        shownSeconds = s;
        countdownLabel.text = s > 0 ? s + "s" : "";
    }

    
}
