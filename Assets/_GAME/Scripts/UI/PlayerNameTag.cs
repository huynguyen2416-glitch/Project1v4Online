using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tên trên đầu nhân vật. Gắn vào prefab Survivor và Hunter (cùng PlayerIdentity).
/// Label là Text nằm dưới Canvas World Space. Chữ luôn quay về camera và được phóng to theo khoảng cách
/// để ở xa vẫn đọc được.
/// </summary>
public class PlayerNameTag : NetworkBehaviour
{
    [SerializeField] Text label;
    [SerializeField] bool hideForOwner = true;

    [Header("Cỡ chữ & độ cao")]
    [Tooltip("Cỡ chữ của Text (đặt lúc spawn). 0 = giữ nguyên theo prefab.")]
    [SerializeField] int fontSize = 40;
    [Tooltip("Độ cao của tên so với gốc nhân vật (mét). Hunter cao hơn Survivor thì chỉnh riêng trên prefab Hunter.")]
    [SerializeField] float heightOffset = 0f;
    [Tooltip("Bật: kích thước tên tính theo mét thế giới, nên Survivor và Hunter (khác scale) hiển thị bằng nhau.")]
    [SerializeField] bool useFixedWorldSize = true;
    [Tooltip("Kích thước 1 đơn vị chữ trong thế giới (mét) ở khoảng cách gần. 0.02 là mức thường dùng.")]
    [SerializeField] float worldSize = 0.02f;

    [Header("Theo khoảng cách")]
    [Tooltip("Trong khoảng này (mét) giữ nguyên cỡ chữ; xa hơn thì phóng to dần.")]
    [SerializeField] float nearDistance = 5f;
    [Tooltip("Hệ số phóng to tối đa khi ở xa.")]
    [SerializeField] float maxScale = 20f;
    [Tooltip("Xa hơn khoảng này (mét) thì ẩn tên.")]
    [SerializeField] float maxVisibleDistance = 150f;

    PlayerIdentity id;
    PlayerRoleInfo myRole;      // vai của nhân vật mang tên này
    PlayerRoleInfo localRole;   // vai của nhân vật máy này đang điều khiển
    Transform cam;
    Vector3 baseLocalScale = Vector3.one;
    bool hidden;   // true: tên của chính mình

    public override void OnNetworkSpawn()
    {
        id = GetComponent<PlayerIdentity>();
        myRole = GetComponent<PlayerRoleInfo>();
        if (label != null)
        {
            if (fontSize > 0) label.fontSize = fontSize;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.alignment = TextAnchor.MiddleCenter;
            baseLocalScale = label.transform.localScale;
            if (heightOffset != 0f) label.transform.localPosition += Vector3.up * heightOffset;
        }
        id.PlayerName.OnValueChanged += OnName;
        Refresh();

        // Chỉ ẩn khi đây đúng là nhân vật của máy này. So sánh với PlayerObject của client local,
        // không dùng IsOwner: bot do host sở hữu nên IsOwner = true trên host.
        NetworkObject mine = NetworkManager != null && NetworkManager.LocalClient != null ? NetworkManager.LocalClient.PlayerObject : null;
        hidden = hideForOwner && mine != null && mine == NetworkObject;
        if (hidden && label != null) label.gameObject.SetActive(false);
    }

    public override void OnNetworkDespawn()
    {
        if (id != null) id.PlayerName.OnValueChanged -= OnName;
    }

    void OnName(FixedString64Bytes a, FixedString64Bytes b) => Refresh();

    void Refresh()
    {
        if (label != null && id != null) label.text = id.DisplayName;
    }

    /// <summary>
    /// Luật hiển thị: chỉ Survivor thấy tên Survivor. Hunter không thấy tên Survivor, Survivor không thấy tên Hunter.
    /// Vai của máy này lấy từ PlayerObject local; chưa có thì ẩn (không để lộ tên trong lúc chờ).
    /// </summary>
    bool RoleAllowsView()
    {
        if (myRole == null) return false;
        if (localRole == null)
        {
            NetworkObject mine = NetworkManager != null && NetworkManager.LocalClient != null ? NetworkManager.LocalClient.PlayerObject : null;
            if (mine != null) localRole = mine.GetComponent<PlayerRoleInfo>();
            if (localRole == null) return false;
        }
        return localRole.CurrentRole == Role.Survivor && myRole.CurrentRole == Role.Survivor;
    }

    void LateUpdate()
    {
        if (label == null || hidden) return;

        if (!RoleAllowsView())
        {
            if (label.enabled) label.enabled = false;
            return;
        }

        if (cam == null && Camera.main != null) cam = Camera.main.transform;
        if (cam == null) return;

        float dist = Vector3.Distance(cam.position, label.transform.position);
        bool show = dist <= maxVisibleDistance;
        if (label.enabled != show) label.enabled = show;
        if (!show) return;

        label.transform.rotation = cam.rotation;                                    // quay về camera
        float k = Mathf.Clamp(dist / nearDistance, 1f, maxScale);   // giữ cỡ chữ gần như không đổi
        if (useFixedWorldSize && label.transform.parent != null)
        {
            float parentScale = Mathf.Max(0.0001f, label.transform.parent.lossyScale.x);   // bù scale của prefab (Hunter hay to/nhỏ khác Survivor)
            label.transform.localScale = Vector3.one * (worldSize * k / parentScale);
        }
        else label.transform.localScale = baseLocalScale * k;
    }
}