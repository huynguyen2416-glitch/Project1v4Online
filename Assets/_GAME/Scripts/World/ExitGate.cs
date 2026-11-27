using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Cổng thoát. Chưa đủ 5 máy: khóa (xám). Đủ máy: kích hoạt (vàng), Survivor nhấn F, thanh tiến độ tự chạy
/// ~10 giây (không có skill check), xong thì cổng mở (xanh). Vào vùng ExitPoint là được gắn cờ "đã thoát".
/// Hunter không dùng được cổng. Server kiểm tra thoát bằng khoảng cách (CharacterController của người khác
/// bị tắt trên máy server nên không dùng được OnTrigger).
/// </summary>
public class ExitGate : NetInteractable
{
    [SerializeField] Transform exitPoint;        // object đặt NGOÀI cửa (đừng đặt làm con của cổng vì scale bị méo)
    [SerializeField] float escapeRadius = 2.5f;

    Collider blocker;

    public readonly NetworkVariable<bool> Powered = new NetworkVariable<bool>(false);

    public override string PromptText => "Mở cổng";
    /// <summary>Điểm thoát (ngoài cửa). Bot đi tới đây để được tính là đã thoát.</summary>
    public Vector3 ExitPosition => exitPoint != null ? exitPoint.position : transform.position;
    public override bool IsAvailable => Powered.Value && !Done.Value;

    void Reset() { duration = 10f; }   // khi vừa Add Component

    public void SetPowered(bool value)
    {
        if (IsServer) Powered.Value = value;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        Done.OnValueChanged += OnGateDoneChanged;
    }

    public override void OnNetworkDespawn()
    {
        Done.OnValueChanged -= OnGateDoneChanged;
        base.OnNetworkDespawn();
    }

    void OnGateDoneChanged(bool _, bool done)
    {
        if (done) SoundManager.I?.PlaySFX(SoundManager.I.gateOpen, transform.position);
    }

    protected override Color GetColor()
    {
        if (Done.Value) return Color.green;
        return Powered.Value ? Color.yellow : Color.gray;
    }

    protected override void Update()
    {
        base.Update();

        // Cổng đã mở: tắt collider để đi xuyên qua được (mọi máy tự làm).
        if (Done.Value)
        {
            if (blocker == null) blocker = GetComponent<Collider>();
            if (blocker != null && blocker.enabled) blocker.enabled = false;
        }

        if (!IsServer || !Done.Value) return;

        Vector3 p = exitPoint != null ? exitPoint.position : transform.position;
        // Quét người chơi đang kết nối
        foreach (ulong id in NetworkManager.ConnectedClientsIds)
        {
            // Người đang Gục vẫn thoát được nếu bò tới cổng.
            if (!TryGetSurvivor(id, out Transform t, true)) continue;
            Vector3 d = t.position - p;
            d.y = 0f;
            if (d.magnitude <= escapeRadius && MatchManager.Instance != null)
                MatchManager.Instance.OnSurvivorEscaped(id);
        }

        // Quét cả Survivor Bot đang thi đấu thay
        foreach (var kv in BotController.ActiveBots)
        {
            ulong botId = kv.Key;
            BotController bot = kv.Value;
            if (bot == null || bot.RoleInfo == null || bot.RoleInfo.CurrentRole != Role.Survivor) continue;
            if (bot.Survivor != null && bot.Survivor.IsOut) continue;

            Vector3 d = bot.transform.position - p;
            d.y = 0f;
            if (d.magnitude <= escapeRadius && MatchManager.Instance != null)
                MatchManager.Instance.OnSurvivorEscaped(botId);
        }
    }
}
