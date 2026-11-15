using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Tìm đường cho bot bằng NavMesh (chỉ TÍNH đường, không dùng NavMeshAgent): nhân vật vẫn đi bằng CharacterController
/// qua NetworkPlayerMovement, nên không có gì phải đồng bộ thêm.
/// Chưa có NavMesh thì tự đi thẳng tới đích (kém thông minh nhưng không lỗi) và ghi cảnh báo 1 lần.
///
/// So với bản trước, các thay đổi nhằm bớt "đi đần":
///   1. Tin đường NavMesh: ở chế độ NavMesh KHÔNG còn dùng tia tránh tường (Avoid) đè lên hướng đi. Đường NavMesh đã tránh tường,
///      còn Avoid hất bot lệch khỏi góc rẽ nên bot cọ tường, đi zích zắc và hay kẹt ở cửa.
///   2. Không tính lại đường cứng nhắc mỗi 0,5 giây: đích đứng yên thì chỉ tính lại mỗi repathInterval và CHỈ đổi sang đường
///      mới khi ngắn hơn ít nhất switchPathGain mét (tránh đổi ý giữa hai đường dài gần bằng nhau).
///   3. Góc rẽ: cornerReach nhỏ hơn và biết bỏ qua góc thừa khi NavMesh cho thấy đi thẳng được tới góc kế tiếp.
///   4. Làm mượt hướng đi (tăng tốc quay) để không giật mỗi lần đổi hướng.
///   5. Đường chỉ tới được một phần (PathPartial): tới cuối đường mà chưa tới đích thì báo kẹt cho brain bỏ mục tiêu,
///      không đứng đó mãi.
///   6. Gỡ kẹt: chọn bên có chỗ trống thay vì ngẫu nhiên.
/// </summary>
public class BotNavigator : MonoBehaviour
{
    public enum NavMode { Idle, NavMesh, Direct, Fallback, Unstick }

    [Header("Đường đi")]
    [Tooltip("Tới gần góc rẽ trong ngần này thì chuyển sang góc kế tiếp. Nhỏ hơn bản cũ (0.7) để không cắt góc.")]
    [SerializeField] float cornerReach = 0.45f;
    [Tooltip("Đích đứng yên: tính lại đường mỗi ngần này giây (chỉ đổi đường khi tốt hơn rõ rệt). Đích đổi chỗ thì tính lại ngay.")]
    [SerializeField] float repathInterval = 1.5f;
    [Tooltip("Chỉ chuyển sang đường mới khi nó ngắn hơn đường đang đi ít nhất ngần này mét.")]
    [SerializeField] float switchPathGain = 1.0f;
    [SerializeField] float destinationMoveThreshold = 1.5f;
    [Tooltip("Tìm điểm NavMesh gần chân bot trong bán kính này. Đừng để lớn: quá 2m có thể dính sang phía bên kia tường.")]
    [SerializeField] float sourceSampleRadius = 1.5f;
    [SerializeField] float destSampleRadius = 3f;
    [Tooltip("Còn cách đích dưới ngần này VÀ NavMesh xác nhận không có gì chắn thì đi thẳng (đích thường nằm sát collider nên NavMesh không dẫn tới tận nơi).")]
    [SerializeField] float directRange = 5f;

    [Header("Chuyển động")]
    [Tooltip("Tốc độ đổi hướng của lệnh di chuyển (đơn vị/giây). Quay ngược 180 độ mất khoảng 2/giá trị giây. 0 = tắt làm mượt.")]
    [SerializeField] float turnAccel = 12f;

    [Header("Tránh tường (chỉ dùng khi KHÔNG có NavMesh, đi thẳng, hoặc gỡ kẹt) và gỡ kẹt")]
    [SerializeField] float probeDistance = 1.0f;
    [Tooltip("Độ cao (từ chân) của tia dò tường. Thấp hơn ngần này một chút là bậc thềm, không coi là tường.")]
    [SerializeField] float probeHeight = 0.9f;
    [SerializeField] LayerMask obstacleMask = ~0;
    [SerializeField] float stuckSeconds = 0.8f;
    [SerializeField] float unstickSeconds = 0.7f;

    NavMeshPath path;
    Vector3[] corners = new Vector3[0];
    int cornerIndex;
    Vector3 destination;
    Vector3 destOnMesh;
    float stopDistance;
    bool hasDestination;
    bool fallback;
    bool pathPartial;
    float partialTimer;
    float nextRepath;

    CharacterController cc;
    Vector3 lastPos;
    Vector3 lastDesired;
    Vector3 unstickDir;
    Vector2 smoothMove;
    float stuckTimer;
    float unstickUntil;
    float lastStuckTime = -99f;
    int stuckCount;
    bool stuckFlag;
    bool warned;
    bool settingsChecked;
    bool radiusSafe = true;   // CharacterController.radius không lớn hơn Agent Radius: mới được cắt góc

    public NavMode Mode { get; private set; } = NavMode.Idle;
    public bool HasDestination => hasDestination;
    public Vector3 Destination => destination;
    /// <summary>Đường hiện tại chỉ tới được một phần: đích nằm ở vùng NavMesh không nối với chỗ bot đứng.</summary>
    public bool PathPartial => pathPartial;

    /// <summary>Đã tới đủ gần đích (trong stopDistance).</summary>
    public bool Arrived => hasDestination && Flat(destination - transform.position).magnitude <= stopDistance;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
    }

    public void SetDestination(Vector3 dest, float stop)
    {
        stopDistance = Mathf.Max(0.1f, stop);
        bool changed = !hasDestination || Flat(dest - destination).magnitude > destinationMoveThreshold;
        destination = dest;
        hasDestination = true;
        if (changed) Repath(true);
    }

    public void Stop()
    {
        hasDestination = false;
        corners = new Vector3[0];
        Mode = NavMode.Idle;
        smoothMove = Vector2.zero;
        ClearStuck();
    }

    /// <summary>true nếu bot kẹt liên tục (đã thử gỡ kẹt mà vẫn không đi được). Brain nên bỏ mục tiêu này. Gọi là xóa cờ.</summary>
    public bool ConsumeStuck()
    {
        bool s = stuckFlag;
        stuckFlag = false;
        return s;
    }

    /// <summary>Gọi mỗi khung hình khi nhân vật được phép đi. Trả về hướng đi trong thế giới (x, z), (0,0) nếu đứng yên.</summary>
    public Vector2 Steer()
    {
        if (!hasDestination) { Mode = NavMode.Idle; ClearStuck(); smoothMove = Vector2.zero; return Vector2.zero; }

        Vector3 pos = transform.position;
        float distToDest = Flat(destination - pos).magnitude;
        if (distToDest <= stopDistance) { Mode = NavMode.Idle; ClearStuck(); smoothMove = Vector2.zero; return Vector2.zero; }

        if (Time.time >= nextRepath) Repath(false);

        // Đang gỡ kẹt: bước sang bên một chút.
        if (Time.time < unstickUntil)
        {
            Mode = NavMode.Unstick;
            lastPos = pos;
            return Smooth(Avoid(unstickDir));
        }

        TrackStuck(pos);
        CheckPartialEnd(pos);

        Vector3 aim = ChooseAim(pos, distToDest);
        Vector3 d = Flat(aim - pos);
        if (d.sqrMagnitude < 0.0001f) return Smooth(Vector3.zero);
        d.Normalize();
        lastDesired = d;

        // Có NavMesh dẫn đường thì tin đường đó; chỉ dùng tia tránh tường khi không có NavMesh hoặc đang đi thẳng.
        return Smooth(Mode == NavMode.NavMesh ? d : Avoid(d));
    }

    /// <summary>Làm mượt lệnh di chuyển để không đổi hướng đột ngột.</summary>
    Vector2 Smooth(Vector3 dir)
    {
        Vector2 target = ToVec2(dir);
        if (turnAccel <= 0f) return target;
        smoothMove = Vector2.MoveTowards(smoothMove, target, turnAccel * Time.deltaTime);
        return smoothMove;
    }

    // ---------- Chọn điểm hướng tới ----------

    Vector3 ChooseAim(Vector3 pos, float distToDest)
    {
        if (fallback || corners.Length == 0)
        {
            Mode = NavMode.Fallback;
            return destination;
        }

        // Gần đích và NavMesh cho thấy không có gì chắn: đi thẳng. (Không áp dụng khi đường chỉ tới được một phần.)
        if (!pathPartial && distToDest <= directRange
            && NavMesh.SamplePosition(pos, out NavMeshHit here, 1f, NavMesh.AllAreas)
            && !NavMesh.Raycast(here.position, destOnMesh, out NavMeshHit _, NavMesh.AllAreas))
        {
            Mode = NavMode.Direct;
            return destination;
        }

        Mode = NavMode.NavMesh;
        while (cornerIndex < corners.Length - 1 && Flat(corners[cornerIndex] - pos).magnitude < cornerReach) cornerIndex++;

        // Bỏ qua góc thừa: từ chỗ đang đứng đi thẳng tới góc kế tiếp được (trong NavMesh) thì không cần ghé góc này.
        if (radiusSafe && cornerIndex < corners.Length - 1
            && NavMesh.SamplePosition(pos, out NavMeshHit cur, 1f, NavMesh.AllAreas)
            && !NavMesh.Raycast(cur.position, corners[cornerIndex + 1], out NavMeshHit _, NavMesh.AllAreas))
        {
            cornerIndex++;
        }

        Vector3 prev = pos + Vector3.up * 0.2f;
        for (int i = cornerIndex; i < corners.Length; i++)
        {
            Vector3 c = corners[i] + Vector3.up * 0.2f;
            Debug.DrawLine(prev, c, pathPartial ? Color.yellow : Color.cyan);
            prev = c;
        }
        return corners[cornerIndex];
    }

    /// <summary>Đường chỉ tới được một phần và bot đã tới cuối đoạn đi được: báo kẹt để brain bỏ mục tiêu này.</summary>
    void CheckPartialEnd(Vector3 pos)
    {
        if (!pathPartial || corners.Length == 0) { partialTimer = 0f; return; }
        if (Flat(corners[corners.Length - 1] - pos).magnitude > 1.0f) { partialTimer = 0f; return; }

        partialTimer += Time.deltaTime;
        if (partialTimer > 1f)
        {
            partialTimer = 0f;
            stuckFlag = true;
            Debug.LogWarning($"[Bot] {name}: đích không nối với chỗ bot đứng trên NavMesh (đường chỉ tới được một phần). Bỏ mục tiêu này. Kiểm tra NavMesh quanh {destination}.");
        }
    }

    // ---------- Tránh tường ----------

    /// <summary>Có tường ngay phía trước thì trượt dọc tường thay vì đâm thẳng vào.</summary>
    Vector3 Avoid(Vector3 dir)
    {
        if (dir.sqrMagnitude < 0.0001f) return dir;

        Vector3 origin = transform.position + Vector3.up * probeHeight;
        float radius = cc != null ? Mathf.Max(0.2f, cc.radius * 0.9f) : 0.3f;
        if (!Physics.SphereCast(origin, radius, dir, out RaycastHit hit, probeDistance, obstacleMask, QueryTriggerInteraction.Ignore))
            return dir;

        if (hit.collider.transform.IsChildOf(transform)) return dir;                          // chính mình
        if (hit.collider.GetComponentInParent<PlayerRoleInfo>() != null) return dir;          // người chơi khác
        if (hasDestination && Flat(hit.point - destination).magnitude < 1.5f) return dir;     // đang tiến vào đích (máy, pallet, cửa sổ...)

        Vector3 slide = Vector3.ProjectOnPlane(dir, hit.normal);
        slide.y = 0f;
        if (slide.magnitude < 0.25f)   // đâm thẳng vào tường: chọn hướng dọc tường nghiêng về phía đích
        {
            slide = Vector3.Cross(Vector3.up, hit.normal);
            if (Vector3.Dot(slide, Flat(destination - transform.position)) < 0f) slide = -slide;
        }
        return slide.normalized;
    }

    /// <summary>Khoảng trống phía trước theo hướng dir (tối đa 2 m), bỏ qua chính mình và người chơi khác.</summary>
    float FreeDistance(Vector3 dir)
    {
        Vector3 origin = transform.position + Vector3.up * probeHeight;
        if (Physics.Raycast(origin, dir, out RaycastHit hit, 2f, obstacleMask, QueryTriggerInteraction.Ignore)
            && !hit.collider.transform.IsChildOf(transform)
            && hit.collider.GetComponentInParent<PlayerRoleInfo>() == null)
            return hit.distance;
        return 2f;
    }

    // ---------- Gỡ kẹt ----------

    void TrackStuck(Vector3 pos)
    {
        float speed = Flat(pos - lastPos).magnitude / Mathf.Max(0.0001f, Time.deltaTime);
        lastPos = pos;
        if (speed < 0.3f) stuckTimer += Time.deltaTime; else stuckTimer = 0f;
        if (stuckTimer < stuckSeconds) return;

        stuckTimer = 0f;
        if (Time.time - lastStuckTime > 6f) stuckCount = 0;
        lastStuckTime = Time.time;
        stuckCount++;
        if (stuckCount >= 2) { stuckFlag = true; stuckCount = 0; }   // kẹt lần thứ hai trong 6 giây: báo brain bỏ mục tiêu

        // Bước sang bên có nhiều chỗ trống hơn (và hơi lùi), thay vì ngẫu nhiên.
        Vector3 basis = lastDesired.sqrMagnitude > 0.01f ? lastDesired : transform.forward;
        Vector3 left = Quaternion.Euler(0f, -90f, 0f) * basis;
        Vector3 right = Quaternion.Euler(0f, 90f, 0f) * basis;
        float fl = FreeDistance(left), fr = FreeDistance(right);
        Vector3 side = Mathf.Abs(fl - fr) < 0.3f ? (Random.value < 0.5f ? left : right) : (fl > fr ? left : right);

        unstickDir = side * 0.9f - basis * 0.45f;
        unstickDir.y = 0f;
        unstickDir.Normalize();
        unstickUntil = Time.time + unstickSeconds;
        Repath(true);
    }

    void ClearStuck()
    {
        stuckTimer = 0f;
        partialTimer = 0f;
        lastPos = transform.position;
    }

    // ---------- Tính đường ----------

    /// <summary>
    /// force = true (đích đổi, vừa kẹt): luôn dùng đường mới. force = false (định kỳ): chỉ đổi khi đường mới ngắn hơn
    /// đường còn lại ít nhất switchPathGain mét, hoặc đường cũ không còn dùng được.
    /// </summary>
    void Repath(bool force)
    {
        nextRepath = Time.time + repathInterval;
        if (path == null) path = new NavMeshPath();   // không tạo ở field: Unity cấm trong MonoBehaviour
        CheckAgentSettings();

        if (!NavMesh.SamplePosition(transform.position, out NavMeshHit from, sourceSampleRadius, NavMesh.AllAreas) ||
            !NavMesh.SamplePosition(destination, out NavMeshHit to, destSampleRadius, NavMesh.AllAreas))
        {
            UseFallback("bot đứng ngoài vùng NavMesh hoặc đích không nằm gần NavMesh");
            return;
        }
        destOnMesh = to.position;

        if (!NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, path)
            || path.status == NavMeshPathStatus.PathInvalid || path.corners.Length == 0)
        {
            UseFallback("không tính được đường");
            return;
        }

        Vector3[] fresh = path.corners;
        bool isPartial = path.status == NavMeshPathStatus.PathPartial;

        if (!force && !fallback && corners.Length > 0 && !pathPartial && !isPartial)
        {
            float oldLeft = Flat(corners[cornerIndex] - transform.position).magnitude + Length(corners, cornerIndex);
            float newLeft = Length(fresh, 0);
            if (newLeft > oldLeft - switchPathGain) return;   // đường cũ vẫn tốt: giữ nguyên, không đổi ý
        }

        fallback = false;
        pathPartial = isPartial;
        corners = fresh;
        cornerIndex = corners.Length > 1 ? 1 : 0;
    }

    static float Length(Vector3[] c, int from)
    {
        float len = 0f;
        for (int i = Mathf.Max(1, from + 1); i < c.Length; i++) len += Vector3.Distance(c[i - 1], c[i]);
        return len;
    }

    void UseFallback(string reason)
    {
        fallback = true;
        pathPartial = false;
        corners = new Vector3[0];
        if (warned) return;
        warned = true;
        Debug.LogWarning($"[Bot] {name}: {reason}. Đang đi thẳng tới đích nên dễ kẹt tường. Kiểm tra đã bake NavMesh chưa và bot có đứng trong vùng xanh không.");
    }

    /// <summary>Cảnh báo nếu CharacterController to hơn Agent Radius của NavMesh: đường đi sẽ sát tường hơn thân bot cho phép.</summary>
    void CheckAgentSettings()
    {
        if (settingsChecked) return;
        settingsChecked = true;
        if (cc == null) return;

        NavMeshBuildSettings s = NavMesh.GetSettingsByID(0);
        Debug.Log($"[Bot] CharacterController.radius = {cc.radius:F2}, NavMesh Agent Radius = {s.agentRadius:F2}, Agent Height = {s.agentHeight:F2}, Step Height = {s.agentClimb:F2}.");
        if (cc.radius > s.agentRadius + 0.05f)
        {
            radiusSafe = false;
            Debug.LogWarning($"[Bot] CharacterController.radius ({cc.radius:F2}) lớn hơn Agent Radius của NavMesh ({s.agentRadius:F2}) nên bot hay cọ tường. " +
                             "Tăng Agent Radius (Window > AI > Navigation > Agents, loại Humanoid) bằng hoặc lớn hơn CharacterController.radius rồi bake lại.");
        }
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    static Vector2 ToVec2(Vector3 v) => new Vector2(v.x, v.z);
}
