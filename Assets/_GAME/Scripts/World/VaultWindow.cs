using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Cửa sổ để nhảy qua. Gắn vào 1 Empty đặt ĐÚNG GIỮA ô cửa sổ.
///  - Trục XANH (Z) của object phải đi XUYÊN qua cửa sổ (vuông góc với bức tường).
///  - Trục ĐỎ (X) chạy dọc theo bề ngang cửa sổ.
///  - Chỉ xoay quanh trục Y, Scale để (1, 1, 1).
/// Chọn object trong Scene view sẽ thấy: hộp vàng = vùng đứng để nhảy, 2 chấm xanh = điểm đáp mỗi bên.
/// Cửa sổ cần 1 BoxCollider (không tick Is Trigger) chắn ô cửa, cao khoảng 1m, để người chơi không đi bộ xuyên qua được.
/// Script này không đồng bộ gì qua mạng: việc nhảy do PlayerVault của chính người chơi xử lý.
/// </summary>
public class VaultWindow : MonoBehaviour
{
    public static readonly List<VaultWindow> All = new List<VaultWindow>();

    [Tooltip("Bề ngang vùng đứng được để nhảy (dọc trục X).")]
    [SerializeField] float width = 2f;
    [Tooltip("Đứng cách tâm cửa sổ tối đa bao nhiêu mét (dọc trục Z) thì được nhảy.")]
    [SerializeField] float approachDepth = 1.2f;
    [Tooltip("Điểm đáp cách tâm cửa sổ bao xa ở phía bên kia.")]
    [SerializeField] float landingDistance = 1.3f;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    /// <summary>Hướng đi xuyên cửa sổ (nằm ngang, độ dài 1).</summary>
    Vector3 Forward
    {
        get
        {
            Vector3 f = transform.forward;
            f.y = 0f;
            return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
        }
    }

    Vector3 Right => Vector3.Cross(Vector3.up, Forward);

    /// <summary>
    /// Người chơi có đang đứng trong vùng nhảy không? side = +1 nếu đứng phía +Z, -1 nếu phía -Z.
    /// Tính theo mét thật nên không bị ảnh hưởng bởi scale của object.
    /// </summary>
    public bool TryGetSide(Vector3 worldPos, out int side, out float distance)
    {
        Vector3 d = worldPos - transform.position;
        d.y = 0f;
        float x = Vector3.Dot(d, Right);
        float z = Vector3.Dot(d, Forward);
        side = z >= 0f ? 1 : -1;
        distance = d.magnitude;
        return Mathf.Abs(x) <= width * 0.5f && Mathf.Abs(z) <= approachDepth;
    }

    /// <summary>Hướng nhảy khi đứng ở phía fromSide (đi sang phía đối diện).</summary>
    public Vector3 JumpDirection(int fromSide) => -fromSide * Forward;

    /// <summary>Điểm đáp ở phía bên kia, giữ nguyên độ cao y của người chơi.</summary>
    public Vector3 GetLandingPoint(int fromSide, float y)
    {
        Vector3 p = transform.position + JumpDirection(fromSide) * landingDistance;
        p.y = y;
        return p;
    }

    void OnDrawGizmosSelected()
    {
        Vector3 f = Forward;
        Quaternion rot = Quaternion.LookRotation(f, Vector3.up);

        Gizmos.color = Color.yellow;
        Gizmos.matrix = Matrix4x4.TRS(transform.position, rot, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(width, 0.1f, approachDepth * 2f));
        Gizmos.matrix = Matrix4x4.identity;

        Gizmos.color = Color.green;
        Gizmos.DrawSphere(transform.position + f * landingDistance, 0.12f);
        Gizmos.DrawSphere(transform.position - f * landingDistance, 0.12f);
        Gizmos.DrawLine(transform.position - f * landingDistance, transform.position + f * landingDistance);
    }
}
