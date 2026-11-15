using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Hầm ngục. Đặt 3 cái (hoặc nhiều hơn) rải trong map Match, mỗi cái gắn NetworkObject + script này,
/// rồi kéo vào ô "Dungeons" của MatchManager. Server chọn ngẫu nhiên 1 cái:
///  - Đủ 2 máy: hầm LỘ ra (mọi người thấy), nhưng chưa dùng được.
///  - Chỉ còn 1 Survivor sống trên bản đồ: hầm MỞ, Survivor đó nhấn F để nhảy xuống => thoát NGAY (tức thì, không có thanh tiến độ).
/// Hầm không được chọn luôn ẩn và không cản đường (tắt cả Renderer lẫn Collider).
/// </summary>
public class Dungeon : NetInteractable
{
    public readonly NetworkVariable<bool> Revealed = new NetworkVariable<bool>(false);
    public readonly NetworkVariable<bool> Open = new NetworkVariable<bool>(false);

    Renderer[] renderers;
    Collider[] colliders;

    public override string PromptText => "Nhảy xuống hầm";
    public override bool IsAvailable => Revealed.Value && Open.Value && !Done.Value;

    public override bool IsInstant => true;   // nhảy xuống là xong ngay, không có thanh tiến độ

    public void ServerReveal() { if (IsServer) Revealed.Value = true; }
    public void ServerOpen() { if (IsServer) Open.Value = true; }

    protected override Color GetColor() => Open.Value ? Color.green : new Color(0.55f, 0.25f, 0.1f);

    protected override void Update()
    {
        base.Update();

        // Chưa lộ thì ẩn hẳn khỏi mọi máy (cả hình lẫn va chạm).
        if (renderers == null) renderers = GetComponentsInChildren<Renderer>();
        if (colliders == null) colliders = GetComponentsInChildren<Collider>();
        bool show = Revealed.Value;
        foreach (Renderer r in renderers)
            if (r != null && r.enabled != show) r.enabled = show;
        foreach (Collider c in colliders)
            if (c != null && c.enabled != show) c.enabled = show;
    }

    protected override void OnCompleted()
    {
        if (MatchManager.Instance == null) return;
        foreach (ulong id in finishers) MatchManager.Instance.OnSurvivorEscaped(id);
    }
}
