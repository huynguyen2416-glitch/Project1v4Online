using UnityEngine;

/// <summary>Kiểm tra vật cản môi trường (tường, cột...) giữa hai điểm. Bỏ qua mọi người chơi.</summary>
public static class LineOfSight
{
    static readonly RaycastHit[] buf = new RaycastHit[16];

    /// <summary>
    /// true nếu từ ngực điểm from tới ngực điểm to không có vật cản.
    /// ignoreRoot: object đích (vd. ghế) để tia không bị chính collider của nó chặn.
    /// </summary>
    public static bool Clear(Vector3 fromFoot, Vector3 toFoot, float eyeHeight, LayerMask mask, Transform ignoreRoot = null)
    {
        Vector3 from = fromFoot + Vector3.up * eyeHeight;
        Vector3 to = toFoot + Vector3.up * eyeHeight;
        Vector3 dir = to - from;
        float dist = dir.magnitude;
        if (dist < 0.01f) return true;

        int n = Physics.RaycastNonAlloc(from, dir / dist, buf, dist, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            Collider c = buf[i].collider;
            if (c.GetComponentInParent<PlayerRoleInfo>() != null) continue;           // người chơi không chặn
            if (ignoreRoot != null && c.transform.IsChildOf(ignoreRoot)) continue;     // chính vật đích
            return false;
        }
        return true;
    }

    /// <summary>Khoảng cách tới vật cản môi trường đầu tiên theo hướng dir (tối đa maxDist). Bỏ qua người chơi.</summary>
    public static float FreeDistance(Vector3 origin, Vector3 dir, float maxDist, LayerMask mask)
    {
        int n = Physics.RaycastNonAlloc(origin, dir, buf, maxDist, mask, QueryTriggerInteraction.Ignore);
        float best = maxDist;
        for (int i = 0; i < n; i++)
        {
            if (buf[i].collider.GetComponentInParent<PlayerRoleInfo>() != null) continue;
            if (buf[i].distance < best) best = buf[i].distance;
        }
        return best;
    }
}
