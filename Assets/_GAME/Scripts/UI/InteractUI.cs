
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

        string extra = target == null ? GetExtraPrompt() : null;   // nhảy cửa sổ, hoặc gợi ý của Hunter
        bool show = target != null || extra != null;
        if (interactPanel != null && interactPanel.activeSelf != show) interactPanel.SetActive(show);
        if (!show) return;

        // Chỉ có lời nhắc (không có thanh tiến độ).
        if (target == null)
        {
            if (progressSlider != null && progressSlider.gameObject.activeSelf) progressSlider.gameObject.SetActive(false);
            if (promptText != null) promptText.text = extra;
            return;
        }

        // Thứ tức thì (nhảy hầm): chỉ hiện lời nhắc, ẩn thanh tiến độ.
        bool instant = target.IsInstant;
        if (progressSlider != null && progressSlider.gameObject.activeSelf == instant)
            progressSlider.gameObject.SetActive(!instant);
        if (instant)
        {
            if (promptText != null) promptText.text = $"[F] {target.PromptText}";
            return;
        }

        float progress = Mathf.Clamp01(target.Progress.Value);
        int pct = Mathf.RoundToInt(progress * 100f);

        if (promptText != null)
            promptText.text = pi.IsWorking
                ? $"Đang giải mã... {pct}%"
                : $"[F] {target.PromptText}  ({pct}%)";
        if (progressSlider != null) progressSlider.value = progress;
    }

    static string GetExtraPrompt()
    {
        // Bế / thả người (phím F, Q) đứng đầu để không bị pallet hay cửa sổ gần đó che mất.
        HunterCarry hc = HunterCarry.Local;
        if (hc != null && !string.IsNullOrEmpty(hc.Prompt)) return hc.Prompt;

        // Pallet dùng chung khung lời nhắc này, y như cửa sổ: "[Space] Đập pallet / Trèo qua pallet / Phá pallet".
        PalletInteract pi = PalletInteract.Local;
        if (pi != null && pi.Prompt != null) return pi.Prompt;

        PlayerVault pv = PlayerVault.Local;
        if (pv != null && pv.Target != null) return $"[{pv.Key}] Nhảy qua cửa sổ";

        return null;
    }
}
