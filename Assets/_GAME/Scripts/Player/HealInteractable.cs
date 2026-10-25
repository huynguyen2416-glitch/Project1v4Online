using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gắn vào prefab SURVIVOR (cùng object với SurvivorState). Survivor khác lại gần người Bị thương hoặc Gục,
/// nhấn F, thanh tiến độ tự chạy (như giải mã). Xong 1 lần: Gục -> Bị thương, Bị thương -> Khỏe.
/// Người được hồi máu từ Gục tiếp tục được hồi cho tới Khỏe nếu người hồi vẫn giữ.
/// Không tự hồi máu cho chính mình (CanUse chặn). Chỉnh "Duration" (giây cho mỗi bậc) và "Range" trong Inspector.
/// </summary>
[RequireComponent(typeof(SurvivorState))]
public class HealInteractable : NetInteractable
{
    SurvivorState state;

    void Awake()
    {
        state = GetComponent<SurvivorState>();
    }

    public override bool TerrorShockOnUse => true;

    public override string PromptText =>
        state != null && state.Current == SurvivorHealth.Downed ? "Đỡ đồng đội dậy" : "Hồi máu đồng đội";

    public override bool IsAvailable =>
        IsSpawned && state != null &&
        (state.Current == SurvivorHealth.Injured || state.Current == SurvivorHealth.Downed);

    /// <summary>Chặn tự hồi máu: người dùng không được là chủ của nhân vật này.</summary>
    public override bool CanUse(ulong userId) => userId != OwnerClientId;

    protected override void OnCompleted()
    {
        if (state == null) return;
        state.ServerHeal();
        // Vừa từ Gục lên Bị thương: người hồi vẫn giữ thì chạy tiếp bậc sau, khỏi nhấn F lần nữa.
        ServerResetProgress(state.Current == SurvivorHealth.Injured);
    }

    // Đừng tô màu nhân vật theo tiến độ (SurvivorState đang tô màu theo trạng thái).
    
}
