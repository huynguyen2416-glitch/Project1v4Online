
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Lời nhắc "[F] Giải mã máy" và thanh tiến độ trên màn hình người chơi local.
/// Gắn vào Canvas (KHÔNG gắn vào InteractPanel). Đọc dữ liệu từ PlayerInteractor.Local, không đồng bộ gì.
/// </summary>
public class InteractUI : MonoBehaviour
{
    [SerializeField] GameObject interactPanel;
    [SerializeField] Text promptText;
    [SerializeField] Slider progressSlider;   // Min 0, Max 1

    void Update()
    {
        PlayerInteractor pi = PlayerInteractor.Local;
        NetInteractable target = pi != null ? pi.Target : null;

        // ── Ưu tiên 1: đang tương tác với NetInteractable (giải mã, cứu người, mở cổng...) ──
        if (target != null)
        {
            SetPanel(true);

            bool instant = target.IsInstant;
            SetSlider(!instant, instant ? 0f : Mathf.Clamp01(target.Progress.Value));

            if (instant)
            {
                if (promptText != null) promptText.text = $"[F] {target.PromptText}";
            }
            else
            {
                float progress = Mathf.Clamp01(target.Progress.Value);
                int pct = Mathf.RoundToInt(progress * 100f);
                if (promptText != null)
                    promptText.text = pi.IsWorking
                        ? $"Đang thực hiện... {pct}%"
                        : $"[F] {target.PromptText}  ({pct}%)";
            }
            return;
        }

        // ── Ưu tiên 2: tự dậy (SelfRevive) — hiện slider như giải mã ──
        SelfRevive sr = SelfRevive.Local;
        if (sr != null && sr.Prompt != null)
        {
            SetPanel(true);
            float prog = Mathf.Clamp01(sr.Progress.Value);
            SetSlider(true, prog);
            if (promptText != null) promptText.text = sr.Prompt;
            return;
        }

        // ── Ưu tiên 3: lời nhắc không có tiến độ (bế người, pallet, nhảy cửa sổ...) ──
        string extra = GetExtraPrompt();
        if (extra != null)
        {
            SetPanel(true);
            SetSlider(false, 0f);
            if (promptText != null) promptText.text = extra;
            return;
        }

        // ── Không có gì: ẩn panel ──
        SetPanel(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    void SetPanel(bool on)
    {
        if (interactPanel != null && interactPanel.activeSelf != on)
            interactPanel.SetActive(on);
    }

    void SetSlider(bool on, float value)
    {
        if (progressSlider == null) return;
        if (progressSlider.gameObject.activeSelf != on)
            progressSlider.gameObject.SetActive(on);
        if (on) progressSlider.value = value;
    }

    static string GetExtraPrompt()
    {
        // SelfRevive đã xử lý riêng ở trên với slider → không cần check lại ở đây.

        // Bế / thả người (Hunter): đứng đầu để không bị pallet / cửa sổ gần đó che mất.
        HunterCarry hc = HunterCarry.Local;
        if (hc != null && !string.IsNullOrEmpty(hc.Prompt)) return hc.Prompt;

        // Pallet (Survivor đập / trèo; Hunter phá).
        PalletInteract pi = PalletInteract.Local;
        if (pi != null && pi.Prompt != null) return pi.Prompt;

        // Cửa sổ.
        PlayerVault pv = PlayerVault.Local;
        if (pv != null && pv.Target != null) return $"[{pv.Key}] Nhảy qua cửa sổ";

        return null;
    }
}
