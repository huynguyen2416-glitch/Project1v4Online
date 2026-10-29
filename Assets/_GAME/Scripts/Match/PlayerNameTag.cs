using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

public class PlayerNameTag : NetworkBehaviour
{
    [SerializeField] Text label;   // child cao ~2.3 m, loại TextMeshPro 3D (không phải UGUI)
    PlayerIdentity id; Transform cam;

    public override void OnNetworkSpawn()
    {
        id = GetComponent<PlayerIdentity>();
        id.PlayerName.OnValueChanged += OnName;
        Refresh();
        if (IsOwner && label != null) label.gameObject.SetActive(false);   // không cần thấy tên mình
    }
    public override void OnNetworkDespawn() { if (id != null) id.PlayerName.OnValueChanged -= OnName; }
    void OnName(FixedString64Bytes a, FixedString64Bytes b) => Refresh();
    void Refresh() { if (label != null && id != null) label.text = id.DisplayName; }

    void LateUpdate()
    {
        if (label == null || !label.gameObject.activeSelf) return;
        if (cam == null && Camera.main != null) cam = Camera.main.transform;
        if (cam != null) label.transform.rotation = cam.rotation;
    }
}