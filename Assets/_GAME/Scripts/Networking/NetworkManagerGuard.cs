using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gắn vào GameObject NetworkManager trong scene Lobby (cùng chỗ với ConnectionUI, MatchSpawner, NetStatsLogger).
/// NetworkManager sống xuyên scene, nhưng khi quay lại Lobby thì scene nạp thêm một bản NetworkManager nữa.
/// Script này hủy bản mới đó (kèm các script đi cùng) để chỉ còn một bộ duy nhất.
/// </summary>
[DefaultExecutionOrder(-1000)]
public class NetworkManagerGuard : MonoBehaviour
{
    void Awake()
    {
        NetworkManager existing = NetworkManager.Singleton;
        if (existing != null && existing.gameObject != gameObject)
            Destroy(gameObject);
    }
}
