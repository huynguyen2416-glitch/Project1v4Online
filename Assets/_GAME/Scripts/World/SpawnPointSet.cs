using UnityEngine;

/// <summary>
/// Đặt MỘT object này trong mỗi scene bản đồ (Match, bản đồ 2, ...) và kéo 9 điểm spawn vào "Points".
/// Mỗi trận MatchSpawner bốc ngẫu nhiên 5 điểm khác nhau trong số đó cho Hunter và 4 Survivor.
/// Mũi tên xanh (trục Z) của mỗi điểm là hướng nhân vật quay mặt lúc bắt đầu.
/// </summary>
public class SpawnPointSet : MonoBehaviour
{
    [Tooltip("9 điểm, mỗi điểm nằm trong một ô của lưới 3x3 chia bản đồ. Đặt cao hơn mặt sàn khoảng 0,1.")]
    [SerializeField] Transform[] points;

    public int Count => points != null ? points.Length : 0;

    public bool TryGetPose(int index, out Vector3 pos, out float yaw)
    {
        pos = Vector3.zero;
        yaw = 0f;
        if (points == null || index < 0 || index >= points.Length || points[index] == null) return false;
        pos = points[index].position;
        yaw = points[index].eulerAngles.y;
        return true;
    }

    // Vẽ vòng tròn và mũi tên hướng nhìn trong Scene view để dễ đặt điểm.
    void OnDrawGizmos()
    {
        if (points == null) return;
        Gizmos.color = Color.cyan;
        foreach (Transform p in points)
        {
            if (p == null) continue;
            Gizmos.DrawWireSphere(p.position, 0.5f);
            Gizmos.DrawRay(p.position, p.forward * 1.5f);
        }
    }
}