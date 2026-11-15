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

    [Header("Theo khoảng cách")]
    [Tooltip("Trong khoảng này (mét) giữ nguyên cỡ chữ; xa hơn thì phóng to dần.")]
    [SerializeField] float nearDistance = 6f;
    [Tooltip("Hệ số phóng to tối đa khi ở xa.")]
    [SerializeField] float maxScale = 6f;
    [Tooltip("Xa hơn khoảng này (mét) thì ẩn tên.")]
    [SerializeField] float maxVisibleDistance = 60f;

    PlayerIdentity id;
    Transform cam;
    bool hidden;   // true: tên của chính mình

    public override void OnNetworkSpawn()
    {
        id = GetComponent<PlayerIdentity>();
        id.PlayerName.OnValueChanged += OnName;
        Refresh();

        hidden = hideForOwner && IsOwner;
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

    void LateUpdate()
    {
        if (label == null || hidden) return;

        if (cam == null && Camera.main != null) cam = Camera.main.transform;
        if (cam == null) return;

        float dist = Vector3.Distance(cam.position, label.transform.position);
        bool show = dist <= maxVisibleDistance;
        if (label.enabled != show) label.enabled = show;
        if (!show) return;

        label.transform.rotation = cam.rotation;                                    // quay về camera
        label.transform.localScale = Vector3.one * Mathf.Clamp(dist / nearDistance, 1f, maxScale);   // giữ cỡ chữ gần như không đổi
    }
}
