using UnityEngine;

public class ThirdPersonCamera : MonoBehaviour
{
    [Header("Mục tiêu theo dõi")]
    public Transform target;

    [Header("Cấu hình Camera")]
    public float distance    = 2.5f;
    public float sensitivity = 3f;
    public float offsetX     = 0f;
    public float offsetY     = 2f;

    [Header("Giới hạn góc nhìn")]
    public float minY = -15f;
    public float maxY =  60f;

    [Header("Chống xuyên tường")]
    [Tooltip("Bật kiểm tra va chạm tường (bỏ qua Trigger và người chơi).")]
    [SerializeField] bool wallClip = false;
    [SerializeField] LayerMask obstacleMask = ~0;
    [SerializeField] float clipRadius = 0.15f;

    float rotationX;
    float rotationY;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;
    }

    /// <summary>
    /// SpectatorController gọi khi chuyển người xem.
    /// </summary>
    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
    }

    void Update()
    {
        if (target == null) return;
        if (Cursor.lockState != CursorLockMode.Locked) return;

        rotationX += Input.GetAxis("Mouse X") * sensitivity;
        rotationY -= Input.GetAxis("Mouse Y") * sensitivity;
        rotationY  = Mathf.Clamp(rotationY, minY, maxY);
    }

    void LateUpdate()
    {
        if (target == null) return;

        Quaternion rotation = Quaternion.Euler(rotationY, rotationX, 0);
        Vector3 pivot       = target.position + Vector3.up * offsetY;
        Vector3 desired     = pivot
                              - rotation * Vector3.forward * distance
                              + rotation * Vector3.right   * offsetX;

        // Chỉ kiểm tra tường khi bật, và bỏ qua trigger + người chơi
        if (wallClip)
        {
            Vector3 dir  = desired - pivot;
            float   dist = dir.magnitude;
            if (Physics.SphereCast(pivot, clipRadius, dir.normalized, out RaycastHit hit, dist, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                // Không va vào chính target
                if (hit.transform != target && !hit.transform.IsChildOf(target))
                {
                    desired = hit.point + hit.normal * clipRadius;
                }
            }
        }

        // Bám trực tiếp theo vị trí nhân vật, không dùng Lerp liên tục gây rung/giật khung hình
        transform.position = desired;
        transform.rotation = rotation;
    }
}

