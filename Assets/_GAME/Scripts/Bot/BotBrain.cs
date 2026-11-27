using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lớp cha của cây quyết định. Think() được BotController gọi khoảng 4 lần/giây; brain chỉ đặt đích cho BotNavigator
/// và bấm phím qua BotInput, không tự di chuyển. Đọc thế giới qua các danh sách static All và NetworkVariable.
/// </summary>
public abstract class BotBrain
{
    protected readonly BotController bot;
    readonly Dictionary<Object, float> ignoredUntil = new Dictionary<Object, float>();

    static PlayerRoleInfo[] players = new PlayerRoleInfo[0];
    static float playersAt = -99f;

    protected BotBrain(BotController bot) { this.bot = bot; }

    protected Transform Me => bot.transform;

    /// <summary>Việc bot đang làm (hiện trên màn hình và ghi vào CSV mỗi khi đổi).</summary>
    public string Goal { get; private set; } = "";

    public abstract void Think();

    protected void SetGoal(string goal)
    {
        if (goal == Goal) return;
        Goal = goal;
        BotLog.Write(bot.Who, "goal", goal);
    }

    protected void Idle(string reason)
    {
        bot.Nav.Stop();
        SetGoal(reason);
    }

    protected void Ignore(Object o, float seconds)
    {
        if (o != null) ignoredUntil[o] = Time.time + seconds;
    }

    protected bool IsIgnored(Object o) => o != null && ignoredUntil.TryGetValue(o, out float until) && Time.time < until;

    protected float FlatTo(Vector3 p)
    {
        Vector3 d = p - Me.position;
        d.y = 0f;
        return d.magnitude;
    }

    protected static float Flat(Vector3 a, Vector3 b)
    {
        Vector3 d = a - b;
        d.y = 0f;
        return d.magnitude;
    }

    /// <summary>
    /// Phía của điểm p so với trục Z (trục xuyên qua) của pallet / cửa sổ: +1 hoặc -1; 0 nếu nằm sát đường giữa (coi như chưa rõ).
    /// </summary>
    protected static int SideOf(Transform obstacle, Vector3 p)
    {
        Vector3 f = obstacle.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 0.0001f) return 0;
        f.Normalize();
        Vector3 d = p - obstacle.position;
        d.y = 0f;
        float z = Vector3.Dot(d, f);
        if (Mathf.Abs(z) < 0.3f) return 0;
        return z > 0f ? 1 : -1;
    }

    /// <summary>a và b cùng một phía của pallet / cửa sổ (hoặc chưa rõ phía) không.</summary>
    protected static bool SameSide(Transform obstacle, Vector3 a, Vector3 b)
    {
        int sa = SideOf(obstacle, a), sb = SideOf(obstacle, b);
        return sa == 0 || sb == 0 || sa == sb;
    }

    /// <summary>
    /// Điểm đứng ngay trước pallet / cửa sổ, trên trục xuyên qua nó, cùng phía với from. Chắc chắn nằm trong vùng tương tác
    /// (pallet: sâu 1,5 m; cửa sổ: sâu 1,2 m), khác với tâm hay hai bên hông là nơi collider chặn bot lại.
    /// </summary>
    protected static Vector3 ApproachPoint(Transform obstacle, Vector3 from, float offset = 0.9f)
    {
        Vector3 f = obstacle.forward;
        f.y = 0f;
        f = f.sqrMagnitude < 0.0001f ? Vector3.forward : f.normalized;
        int side = SideOf(obstacle, from);
        if (side == 0) side = 1;
        Vector3 p = obstacle.position + f * (side * offset);
        p.y = from.y;
        return p;
    }

    /// <summary>
    /// Điểm đứng trên trục xuyên qua pallet / cửa sổ ở phía XA điểm 'awayFrom' (thường là Hunter).
    /// Đứng phía này, pallet nằm giữa mình và Hunter: đập khi Hunter tới sát thì Hunter bị choáng còn mình ở bên an toàn.
    /// Chưa rõ phía của awayFrom (sát đường giữa) thì dùng phía của 'from'.
    /// </summary>
    protected static Vector3 FarSidePoint(Transform obstacle, Vector3 awayFrom, Vector3 from, float offset = 0.9f)
    {
        Vector3 f = obstacle.forward;
        f.y = 0f;
        f = f.sqrMagnitude < 0.0001f ? Vector3.forward : f.normalized;
        int side = -SideOf(obstacle, awayFrom);
        if (side == 0) side = SideOf(obstacle, from);
        if (side == 0) side = 1;
        Vector3 p = obstacle.position + f * (side * offset);
        p.y = from.y;
        return p;
    }

    /// <summary>Có nhìn thấy điểm p (trong tầm, không bị tường chắn) không.</summary>
    protected bool Sees(Vector3 p, float range) => FlatTo(p) <= range && LineOfSight.Clear(Me.position, p, 1f, ~0);

    /// <summary>Hunter gần nhất (không tính chính mình). Danh sách người chơi làm mới mỗi giây.</summary>
    protected Transform NearestHunter(out float dist)
    {
        if (Time.time - playersAt > 1f)
        {
            players = Object.FindObjectsByType<PlayerRoleInfo>(FindObjectsSortMode.None);
            playersAt = Time.time;
        }

        Transform best = null;
        dist = float.MaxValue;
        foreach (PlayerRoleInfo p in players)
        {
            if (p == null || p.gameObject == bot.gameObject || p.CurrentRole != Role.Hunter) continue;
            float d = FlatTo(p.transform.position);
            if (d < dist) { dist = d; best = p.transform; }
        }
        return best;
    }

    /// <summary>Cổng thoát đã mở (Survivor có thể đi qua để thoát), null nếu chưa có.</summary>
    protected static ExitGate FindOpenGate()
    {
        foreach (NetInteractable it in NetInteractable.All)
        {
            ExitGate g = it as ExitGate;
            if (g != null && g.IsSpawned && g.Done.Value) return g;
        }
        return null;
    }
}