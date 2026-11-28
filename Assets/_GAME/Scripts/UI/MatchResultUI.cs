using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Màn hình kết quả cuối trận (tuần 4). Gắn vào Canvas của scene Match (khác với MatchHUD 2 dòng chữ).
/// Khi MatchManager có kết quả: hiện bảng "SURVIVOR THẮNG / HUNTER THẮNG / HÒA", dòng "Bạn đã thắng/thua",
/// số thoát và bị loại, lý do. Mở lại con trỏ chuột, khóa di chuyển của người chơi này.
/// Chỉ host thấy nút "Về phòng chờ"; người khác thấy dòng chờ host.
/// </summary>
public class MatchResultUI : MonoBehaviour
{
    [SerializeField] GameObject panel;
    [SerializeField] Text titleText;
    [SerializeField] Text detailText;
    [Tooltip("Nút Về phòng chờ (chỉ host thấy).")]
    [SerializeField] Button backButton;
    [Tooltip("Tùy chọn: dòng chữ \"Chờ host đưa về phòng chờ\" cho người không phải host.")]
    [SerializeField] GameObject waitHostText;

    bool shown;
    int lastKey = -1;

    void Start()
    {
        if (panel != null) panel.SetActive(false);
        if (backButton != null) backButton.onClick.AddListener(OnBack);
    }

    void OnBack()
    {
        SoundManager.I?.PlayUIClick();
        if (MatchManager.Instance != null) MatchManager.Instance.BackToLobby();
    }

    void Update()
    {
        MatchManager m = MatchManager.Instance;
        if (m == null || !m.IsSpawned) return;

        MatchResult r = (MatchResult)m.Result.Value;
        if (r == MatchResult.None)
        {
            if (shown) { shown = false; lastKey = -1; if (panel != null) panel.SetActive(false); }
            return;
        }

        if (!shown) Show();
        Refresh(m, r);
    }

    void Show()
    {
        shown = true;
        if (panel != null) panel.SetActive(true);

        // Mở lại con trỏ để bấm nút; khóa di chuyển của nhân vật mình.
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        NetworkPlayerMovement mv = GetLocalPlayer() != null ? GetLocalPlayer().GetComponent<NetworkPlayerMovement>() : null;
        if (mv != null) mv.SetLocked(this, true);

        bool isHost = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        if (backButton != null) backButton.gameObject.SetActive(isHost);
        if (waitHostText != null) waitHostText.SetActive(!isHost);
    }

    static NetworkObject GetLocalPlayer()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient == null) return null;
        return nm.LocalClient.PlayerObject;
    }

    static Role GetMyRole()
    {
        NetworkObject po = GetLocalPlayer();
        PlayerRoleInfo info = po != null ? po.GetComponent<PlayerRoleInfo>() : null;
        return info != null ? info.CurrentRole : Role.Survivor;
    }

    // Làm mới chữ khi số liệu đổi (Reason và Result có thể tới không cùng lúc nên đọc lại mỗi khung hình).
    void Refresh(MatchManager m, MatchResult r)
    {
        EndReason reason = (EndReason)m.Reason.Value;
        int key = ((int)r * 100 + (int)reason * 10) * 1000 + m.Escaped.Value * 10 + m.Eliminated.Value;
        if (key == lastKey) return;
        lastKey = key;

        string title;
        switch (r)
        {
            case MatchResult.SurvivorsWin: title = "SURVIVOR THẮNG"; break;
            case MatchResult.HunterWins: title = "HUNTER THẮNG"; break;
            default: title = "HÒA"; break;
        }

        Role me = GetMyRole();
        bool iWin = (r == MatchResult.SurvivorsWin && me == Role.Survivor) || (r == MatchResult.HunterWins && me == Role.Hunter);
        string myLine = r == MatchResult.Draw ? "Hai bên hòa" : (iWin ? "Bạn đã thắng!" : "Bạn đã thua");

        if (titleText != null) titleText.text = title;
        if (detailText != null)
            detailText.text = $"{myLine}\nThoát: {m.Escaped.Value}    Bị loại: {m.Eliminated.Value}\nLý do: {ReasonText(reason)}";
    }

    static string ReasonText(EndReason reason)
    {
        switch (reason)
        {
            case EndReason.EnoughEscaped: return "đủ số Survivor thoát";
            case EndReason.EnoughEliminated: return "đủ số Survivor bị loại";
            case EndReason.AllResolved: return "mọi Survivor đã có kết quả";
            case EndReason.TimeUp: return "hết giờ";
            case EndReason.HunterLeft: return "Hunter rời trận";
            default: return "";
        }
    }
}
