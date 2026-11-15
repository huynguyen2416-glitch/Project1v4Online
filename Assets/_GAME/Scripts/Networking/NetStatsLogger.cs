using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Ghi log mạng ra CSV (mỗi tiến trình một file) để làm số liệu E0-E3 và báo cáo. Gắn vào NetworkManager.
/// Cột: time_s,type,role,clientId,rttMs,fps,detail,lossPct,inKBps,posErrM,hitDelayMs
///   type = sample  (mỗi interval giây: rttMs, fps, lossPct, inKBps)
///        | event   (kết nối / ngắt kết nối / bắt đầu / dừng / config / mốc Mark)
///        | poserr  (server: một dòng cho mỗi lần đo lệch vị trí, cột posErrM)
///        | hit     (server: một dòng cho mỗi đòn đánh, cột hitDelayMs)
/// lossPct và inKBps lấy từ NetProbe nếu có cùng GameObject; không có thì để trống.
/// Dòng event "config" ghi mức giả lập (NetSimulator.Label) để script phân tích gom theo E0-E3.
/// File nằm ở Application.persistentDataPath (đường dẫn được in ra Console khi bắt đầu).
/// </summary>
public class NetStatsLogger : MonoBehaviour
{
    [SerializeField] float intervalSeconds = 1f;

    static NetStatsLogger instance;

    NetworkManager nm;
    NetProbe probe;
    StreamWriter writer;
    float startTime, sampleTimer;
    int frames;
    bool wasListening;

    void Start()
    {
        instance = this;
        nm = NetworkManager.Singleton;
        if (nm == null) { UnityEngine.Debug.LogError("NetStatsLogger: không thấy NetworkManager"); enabled = false; return; }
        probe = GetComponent<NetProbe>();

        string name = $"netlog_{DateTime.Now:yyyyMMdd_HHmmss}_p{Process.GetCurrentProcess().Id}.csv";
        string path = Path.Combine(Application.persistentDataPath, name);
        writer = new StreamWriter(path, false) { AutoFlush = true };
        writer.WriteLine("time_s,type,role,clientId,rttMs,fps,detail,lossPct,inKBps,posErrM,hitDelayMs");
        UnityEngine.Debug.Log("NetStatsLogger ghi vào: " + path);
        startTime = Time.realtimeSinceStartup;

        Write("event", "config " + NetSimulator.Label, "", 0, "", "");

        nm.OnClientConnectedCallback += OnConnected;
        nm.OnClientDisconnectCallback += OnDisconnected;
        NetMetricsHub.HitDelay += OnHitDelay;
        NetMetricsHub.PosError += OnPosError;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        NetMetricsHub.HitDelay -= OnHitDelay;
        NetMetricsHub.PosError -= OnPosError;
        if (nm != null)
        {
            nm.OnClientConnectedCallback -= OnConnected;
            nm.OnClientDisconnectCallback -= OnDisconnected;
        }
        writer?.Dispose();
    }

    void Update()
    {
        if (nm == null) return;

        if (nm.IsListening != wasListening)
        {
            wasListening = nm.IsListening;
            Write("event", nm.IsListening ? "start" : "stop", Role(), nm.LocalClientId, "", "");
        }
        if (!nm.IsListening) return;

        frames++;
        sampleTimer += Time.unscaledDeltaTime;
        if (sampleTimer < intervalSeconds) return;

        string fps = F(frames / sampleTimer, "F1");
        frames = 0; sampleTimer = 0f;

        if (nm.IsServer)
        {
            bool any = false;
            foreach (ulong id in nm.ConnectedClientsIds)
            {
                if (id == NetworkManager.ServerClientId) continue;
                any = true;
                ulong rtt = nm.NetworkConfig.NetworkTransport.GetCurrentRtt(id);
                WriteSample(id, rtt.ToString(), fps);
            }
            if (!any) Write("sample", "", Role(), nm.LocalClientId, "0", fps);
        }
        else if (nm.IsConnectedClient)
        {
            ulong rtt = nm.NetworkConfig.NetworkTransport.GetCurrentRtt(NetworkManager.ServerClientId);
            WriteSample(NetworkManager.ServerClientId, rtt.ToString(), fps);
        }
    }

    void WriteSample(ulong id, string rtt, string fps)
    {
        string loss = "", kbps = "";
        if (probe != null && probe.TryGet(id, out float l, out float k))
        {
            loss = F(l, "F2");
            kbps = F(k, "F2");
        }
        Write("sample", "", Role(), id, rtt, fps, loss, kbps);
    }

    void OnConnected(ulong id) => Write("event", "connected", Role(), id, "", "");
    void OnDisconnected(ulong id) => Write("event", "disconnected", Role(), id, "", "");
    void OnHitDelay(ulong id, float ms) => Write("hit", "", Role(), id, "", "", "", "", "", F(ms, "F1"));
    void OnPosError(ulong id, float m) => Write("poserr", "", Role(), id, "", "", "", "", F(m, "F3"));

    /// <summary>Đánh dấu một mốc vào CSV (type=event). Vd NetStatsLogger.Mark("interact_start") để so RTT/FPS
    /// khi giải mã với khi chỉ đi bộ. Gọi an toàn ở bất kỳ đâu, không có logger thì bỏ qua.</summary>
    public static void Mark(string label)
    {
        if (instance == null || instance.nm == null || instance.writer == null) return;
        instance.Write("event", label.Replace(',', '_'), instance.Role(), instance.nm.LocalClientId, "", "");
    }

    string Role() => nm.IsHost ? "Host" : nm.IsServer ? "Server" : "Client";

    // NaN/Infinity -> ô trống, để script phân tích bỏ qua.
    static string F(float v, string fmt) =>
        float.IsNaN(v) || float.IsInfinity(v) ? "" : v.ToString(fmt, CultureInfo.InvariantCulture);

    void Write(string type, string detail, string role, ulong clientId, string rtt, string fps,
               string loss = "", string kbps = "", string posErr = "", string hitDelay = "")
    {
        if (writer == null) return;
        float t = Time.realtimeSinceStartup - startTime;
        writer.WriteLine($"{t.ToString("F2", CultureInfo.InvariantCulture)},{type},{role},{clientId},{rtt},{fps},{detail},{loss},{kbps},{posErr},{hitDelay}");
    }
}
