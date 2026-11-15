using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// HUD trận chỉ có 2 dòng chữ:
///  - Dòng 1 (machineText): "Máy còn lại cần giải: N". Giải đủ máy thì đổi thành "Kẻ sống sót có thể mở cổng".
///  - Dòng 2 (dungeonText): "Hầm chưa lộ" -> "Hầm đã lộ" -> "Hầm đã mở".
/// Chỉ đọc NetworkVariable của MatchManager; chữ chỉ được gán lại khi giá trị đổi.
/// Gắn vào Canvas.
/// BackButton (tùy chọn): chỉ host nhìn thấy; OnClick -> MatchManager.BackToLobby
/// (kéo object MatchManager vào ô Object). Để trống nếu không dùng nút này.
/// </summary>
public class MatchHUD : MonoBehaviour
{
    [SerializeField] Text machineText;   // dòng 1
    [SerializeField] Text dungeonText;   // dòng 2
    [SerializeField] Button backButton;

    const string GatesReadyLine = "Kẻ sống sót có thể mở cổng";

    int shownRemaining = -1;
    bool shownGates;
    int shownPhase = -1;

    void Update()
    {
        MatchManager m = MatchManager.Instance;
        if (m == null || !m.IsSpawned) return;

        // Dòng 1: số máy còn lại, đủ máy (cổng có điện) thì đổi câu.
        bool gates = m.GatesPowered.Value;
        int remaining = Mathf.Max(0, m.MachinesRequired - m.MachinesDone.Value);
        if (machineText != null && (remaining != shownRemaining || gates != shownGates))
        {
            shownRemaining = remaining;
            shownGates = gates;
            machineText.text = gates ? GatesReadyLine : $"Máy còn lại cần giải: {remaining}";
        }

        // Dòng 2: trạng thái hầm.
        int phase = m.DungeonPhase.Value;
        if (dungeonText != null && phase != shownPhase)
        {
            shownPhase = phase;
            dungeonText.text = DungeonLine(phase);
        }

        if (backButton != null)
        {
            bool isHost = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
            if (backButton.gameObject.activeSelf != isHost) backButton.gameObject.SetActive(isHost);
        }
    }

    static string DungeonLine(int phase)
    {
        switch (phase)
        {
            case MatchManager.DungeonRevealed: return "Hầm đã lộ";
            case MatchManager.DungeonOpen: return "Hầm đã mở";
            default: return "Hầm chưa lộ";
        }
    }
}
