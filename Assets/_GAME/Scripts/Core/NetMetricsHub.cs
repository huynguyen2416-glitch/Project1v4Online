using System;
using UnityEngine;

/// <summary>
/// Cầu nối số đo mạng (tầng 0 Core). Script ở tầng thấp (HunterAttack) chỉ PHÁT sự kiện; NetStatsLogger (tầng Networking)
/// lắng nghe và ghi CSV. Nhờ vậy không có tham chiếu ngược tầng.
/// </summary>
public static class NetMetricsHub
{
    /// <summary>Server: (clientId, mili giây) từ lúc client bấm đánh đến lúc server nhận được lệnh.</summary>
    public static event Action<ulong, float> HitDelay;

    /// <summary>Server: (clientId, mét) lệch giữa vị trí chủ nhân báo lên và bản sao trên server tại cùng thời điểm.</summary>
    public static event Action<ulong, float> PosError;

    public static void RaiseHitDelay(ulong clientId, float ms) => HitDelay?.Invoke(clientId, ms);
    public static void RaisePosError(ulong clientId, float meters) => PosError?.Invoke(clientId, meters);

    // Tắt "Reload Domain" trong Editor thì biến static không tự reset, nên xóa người nghe cũ khi vào Play.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset()
    {
        HitDelay = null;
        PosError = null;
    }
}
