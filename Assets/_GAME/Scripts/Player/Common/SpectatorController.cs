using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
/// <summary>
/// Chế độ khán giả đơn giản (tuần 4). Đặt 1 GameObject trong scene Match, gắn script này, kéo ThirdPersonCamera vào.
/// Khi người chơi này đã thoát hoặc bị loại (và trận chưa kết thúc): camera chuyển sang theo dõi Survivor còn sống
/// (hoặc Hunter nếu không còn ai). Nhấn E để xem người kế tiếp, Q để xem người trước.
/// Không đổi gì ở phía người chơi còn sống.
/// </summary>
public class SpectatorController : MonoBehaviour
{
    [SerializeField] ThirdPersonCamera cam;
    [Tooltip("Tùy chọn: chữ báo đang xem ai .")]
    [SerializeField] Text label;
    [SerializeField] KeyCode nextKey = KeyCode.E;
    [SerializeField] KeyCode prevKey = KeyCode.Q;

    readonly List<Transform> targets = new List<Transform>();
    readonly List<string> names = new List<string>();
    int index;
    float nextRefresh;
    bool spectating;

    void Update()
    {
        if (cam == null)
        {
            cam = FindFirstObjectByType<ThirdPersonCamera>();
            if (cam == null) return;
        }

        NetworkManager nm = NetworkManager.Singleton;
        NetworkObject me = nm != null && nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
        SurvivorState myState = me != null ? me.GetComponent<SurvivorState>() : null;

        bool shouldSpectate = myState != null && myState.IsOut && !MatchManager.Ended;
        if (!shouldSpectate)
        {
            if (spectating) { spectating = false; if (label != null) label.text = ""; }
            return;
        }
        spectating = true;

        if (Time.time >= nextRefresh)
        {
            nextRefresh = Time.time + 0.5f;
            RebuildTargets(me);
        }
        if (targets.Count == 0) return;

        if (Input.GetKeyDown(nextKey)) index++;
        if (Input.GetKeyDown(prevKey)) index--;
        index = (index % targets.Count + targets.Count) % targets.Count;

        Transform t = targets[index];
        if (t != null) cam.target = t;
        if (label != null) label.text = $"Đang xem: {names[index]}   [{prevKey}/{nextKey}] đổi người";
    }

    // Người xem được: Survivor còn sống (trừ mình), rồi tới Hunter.
    void RebuildTargets(NetworkObject me)
    {
        targets.Clear();
        names.Clear();

        Transform hunter = null;
        foreach (PlayerRoleInfo info in FindObjectsByType<PlayerRoleInfo>(FindObjectsSortMode.None))
        {
            if (!info.IsSpawned || info.NetworkObject == me) continue;
            if (info.CurrentRole == Role.Hunter) { hunter = info.transform; continue; }

            SurvivorState st = info.GetComponent<SurvivorState>();
            if (st == null || st.IsOut) continue;
            targets.Add(info.transform);
            PlayerIdentity pid = info.GetComponent<PlayerIdentity>();
            names.Add(pid != null && !string.IsNullOrEmpty(pid.DisplayName) ? pid.DisplayName : "Survivor");
        }
        if (hunter != null) { targets.Add(hunter); names.Add("Hunter"); }
    }
}
