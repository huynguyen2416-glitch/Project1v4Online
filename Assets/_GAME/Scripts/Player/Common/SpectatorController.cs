using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Chế độ khán giả. Gắn vào 1 GameObject trong scene Match, kéo ThirdPersonCamera vào.
///
/// ĐIỀU KIỆN VÀO SPECTATE:
///   - Survivor: IsOut (Escaped hoặc Eliminated) khi trận chưa kết thúc.
///   → Khi vào spectate: ẩn HUD interact, chỉ giữ SurvivorStatusHUD.
///
/// DANH SÁCH THEO DÕI:
///   - Chỉ theo dõi các Survivor đồng đội còn sống (không theo dõi Hunter để tránh lộ vị trí).
///   → Nhấn [E] xem người kế, [Q] xem người trước.
/// </summary>
public class SpectatorController : MonoBehaviour
{
    [SerializeField] ThirdPersonCamera cam;

    [Header("UI")]
    [Tooltip("Panel chứa chữ 'Đang xem: ...' — sẽ bật/tắt tự động.")]
    [SerializeField] GameObject spectatePanel;
    [Tooltip("Text hiện tên người đang được xem.")]
    [SerializeField] Text label;
    [Tooltip("HUD tương tác cần ẩn khi spectating (InteractPanel của InteractUI).")]
    [SerializeField] GameObject interactHUD;

    [Header("Phím")]
    [SerializeField] KeyCode nextKey = KeyCode.E;
    [SerializeField] KeyCode prevKey = KeyCode.Q;

    readonly List<Transform> targets = new List<Transform>();
    readonly List<string>    names   = new List<string>();
    int   index;
    float nextRefresh;
    bool  spectating;

    // ── Unity ─────────────────────────────────────────────────────────────────

    void Update()
    {
        // Lazy-find camera nếu chưa kéo vào Inspector
        if (cam == null)
        {
            cam = FindFirstObjectByType<ThirdPersonCamera>();
            if (cam == null) return;
        }

        bool shouldSpectate = ShouldSpectate(out NetworkObject me);

        if (!shouldSpectate)
        {
            if (spectating) ExitSpectate();
            return;
        }

        if (!spectating) EnterSpectate();

        // Làm mới danh sách mỗi 0.5s
        if (Time.time >= nextRefresh)
        {
            nextRefresh = Time.time + 0.5f;
            int prevCount = targets.Count;
            RebuildTargets(me);

            // Reset index an toàn nếu danh sách co lại
            if (targets.Count > 0 && targets.Count < prevCount)
                index = Mathf.Clamp(index, 0, targets.Count - 1);
        }

        if (targets.Count == 0)
        {
            if (label != null) label.text = "Không còn ai để xem";
            return;
        }

        // Chuyển người xem
        if (Input.GetKeyDown(nextKey)) index++;
        if (Input.GetKeyDown(prevKey)) index--;
        index = (index % targets.Count + targets.Count) % targets.Count;

        Transform t = targets[index];
        if (t != null && cam.target != t) cam.SetTarget(t);   // smooth transition

        if (label != null)
            label.text = $"Đang xem: {names[index]}   [{prevKey}] ◄  ► [{nextKey}]";
    }

    // ── Spectate enter / exit ─────────────────────────────────────────────────

    void EnterSpectate()
    {
        spectating = true;
        if (spectatePanel != null) spectatePanel.SetActive(true);
        if (interactHUD   != null) interactHUD.SetActive(false);   // ẩn prompt tương tác
    }

    void ExitSpectate()
    {
        spectating = false;
        if (spectatePanel != null) spectatePanel.SetActive(false);
        if (interactHUD   != null) interactHUD.SetActive(true);    // trả lại HUD nếu còn sống
        if (label != null) label.text = "";
        // Trả camera về nhân vật của mình
        NetworkObject me = NetworkManager.Singleton?.LocalClient?.PlayerObject;
        if (me != null && cam != null) cam.SetTarget(me.transform);
    }

    // ── Điều kiện vào spectate ────────────────────────────────────────────────

    static bool ShouldSpectate(out NetworkObject me)
    {
        me = null;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient == null) return false;
        me = nm.LocalClient.PlayerObject;
        if (me == null) return false;

        // Chỉ Survivor mới vào spectate
        PlayerRoleInfo role = me.GetComponent<PlayerRoleInfo>();
        if (role != null && role.CurrentRole == Role.Hunter) return false;

        // Survivor: đã thoát hoặc bị loại và trận chưa kết thúc
        SurvivorState ss = me.GetComponent<SurvivorState>();
        if (ss != null) return ss.IsOut && !MatchManager.Ended;

        return false;
    }

    // ── Xây danh sách mục tiêu ────────────────────────────────────────────────

    /// <summary>
    /// Chỉ theo dõi các Survivor đồng đội còn sống.
    /// Không xem bản thân và không xem Hunter (tránh gian lận).
    /// </summary>
    void RebuildTargets(NetworkObject me)
    {
        targets.Clear();
        names.Clear();

        foreach (PlayerRoleInfo info in FindObjectsByType<PlayerRoleInfo>(FindObjectsSortMode.None))
        {
            if (!info.IsSpawned || info.NetworkObject == me) continue;

            // Bỏ Hunter ra: không cho xem Hunter
            if (info.CurrentRole == Role.Hunter) continue;

            // Survivor: chỉ theo dõi người còn sống
            SurvivorState st = info.GetComponent<SurvivorState>();
            if (st == null || st.IsOut) continue;

            PlayerIdentity pid = info.GetComponent<PlayerIdentity>();
            string displayName = (pid != null && !string.IsNullOrEmpty(pid.DisplayName))
                ? pid.DisplayName : "Survivor";

            targets.Add(info.transform);
            names.Add(displayName);
        }
    }
}
