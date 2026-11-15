using UnityEngine;

public class ThirdPersonCamera : MonoBehaviour
{
    [Header("Mục tiêu theo dõi")]
    public Transform target;

    [Header("Cấu hình Camera")]
    public float distance = 2.5f;
    public float sensitivity = 3f;
    public float offsetX = 0f;
    public float offsetY = 2f;

    [Header("Giới hạn góc nhìn")]
    public float minY = -15f;
    public float maxY = 60f;

    private float rotationX = 0f;
    private float rotationY = 0f;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        if (target == null) return;
        //lock nếu chuột mở thao tác khác
        if (Cursor.lockState != CursorLockMode.Locked) return;

        // Đọc chuột ở Update để nhận phản hồi nhanh nhất, không bị miss frame
        rotationX += Input.GetAxis("Mouse X") * sensitivity;
        rotationY -= Input.GetAxis("Mouse Y") * sensitivity;
        rotationY = Mathf.Clamp(rotationY, minY, maxY);
    }

    void LateUpdate()
    {
        if (target == null) return;

        // Tính góc xoay
        Quaternion rotation = Quaternion.Euler(rotationY, rotationX, 0);

        // Tính tâm xoay trên người nhân vật
        Vector3 pivotPosition = target.position + (Vector3.up * offsetY);

        // Tính toán vị trí cần đến
        Vector3 desiredPosition = pivotPosition
                                  - (rotation * Vector3.forward * distance)
                                  + (rotation * Vector3.right * offsetX);

        // camera dí vào chuyển động nhân vật
        transform.position = desiredPosition;
        transform.rotation = rotation;
    }
}
