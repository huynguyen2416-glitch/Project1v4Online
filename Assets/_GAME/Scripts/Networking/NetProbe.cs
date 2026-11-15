using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Đo các chỉ số mà NetworkManager không cho sẵn (câu 5). Gắn cùng GameObject với NetworkManager và NetStatsLogger.
///
///  1. Packet loss (tầng ứng dụng): mỗi bên gửi gói "probe" có số thứ tự, kiểu Unreliable, mặc định 20 gói/giây.
///     Bên nhận so khoảng trống số thứ tự trong mỗi cửa sổ 1 giây. Server đo chiều lên (client -> server),
///     client đo chiều xuống (server -> client).
///  2. Bandwidth nhận (KB/s): cộng kích thước payload của mọi sự kiện Data từ transport. Không gồm header UDP/UTP.
///     Băng thông gửi của một bên chính là băng thông nhận của bên kia.
///  3. Position error (mét): client gửi vị trí nhân vật của mình kèm ServerTime 4 lần/giây; server tra vị trí bản sao
///     của nhân vật đó trong lịch sử 3 giây tại đúng thời điểm ấy rồi tính khoảng cách. Phát qua NetMetricsHub.PosError.
///
/// Nếu bản NGO của nhóm không có NetworkTransport.OnTransportEvent, thêm define NETPROBE_NO_BANDWIDTH
/// (Project Settings > Player > Scripting Define Symbols) để bỏ phần đo bandwidth; cột inKBps sẽ để trống.
/// </summary>
public class NetProbe : MonoBehaviour
{
    const string ProbeMsg = "netprobe.seq";
    const string PosMsg = "netprobe.pos";

    [SerializeField] float probeHz = 20f;
    [SerializeField] float posReportHz = 4f;
    [SerializeField] float windowSeconds = 1f;
    [SerializeField] float historySeconds = 3f;

    class Rx
    {
        public bool started;
        public uint prevMax, max;
        public int count;
        public float lossPct = float.NaN;
        public long bytes;
        public float inKBps = float.NaN;
    }

    struct Sample
    {
        public double t;
        public Vector3 p;
    }

    readonly Dictionary<ulong, Rx> rx = new Dictionary<ulong, Rx>();
    readonly Dictionary<ulong, List<Sample>> history = new Dictionary<ulong, List<Sample>>();

    NetworkManager nm;
    bool registered;
    uint sendSeq;
    float nextProbe, nextPos, windowStart;

    /// <summary>Số đo của cửa sổ 1 giây gần nhất cho nguồn clientId (server: id của client; client: ServerClientId).</summary>
    public bool TryGet(ulong id, out float lossPct, out float inKBps)
    {
        if (rx.TryGetValue(id, out Rx s)) { lossPct = s.lossPct; inKBps = s.inKBps; return true; }
        lossPct = float.NaN;
        inKBps = float.NaN;
        return false;
    }

    void Start()
    {
        nm = NetworkManager.Singleton;
        if (nm == null) { Debug.LogError("NetProbe: không thấy NetworkManager"); enabled = false; return; }
        nm.OnServerStarted += OnStarted;
        nm.OnClientStarted += OnStarted;
        nm.OnServerStopped += OnStopped;
        nm.OnClientStopped += OnStopped;
    }

    void OnDestroy()
    {
        if (nm == null) return;
        nm.OnServerStarted -= OnStarted;
        nm.OnClientStarted -= OnStarted;
        nm.OnServerStopped -= OnStopped;
        nm.OnClientStopped -= OnStopped;
        Unregister();
    }

    void OnStarted()
    {
        if (registered) return;   // Host kích hoạt cả hai sự kiện
        var cm = nm.CustomMessagingManager;
        if (cm == null) return;
        cm.RegisterNamedMessageHandler(ProbeMsg, OnProbe);
        cm.RegisterNamedMessageHandler(PosMsg, OnPos);
#if !NETPROBE_NO_BANDWIDTH
        if (nm.NetworkConfig.NetworkTransport != null)
            nm.NetworkConfig.NetworkTransport.OnTransportEvent += OnTransportEvent;
#endif
        registered = true;
        rx.Clear();
        history.Clear();
        sendSeq = 0;
        windowStart = Time.unscaledTime;
    }

    void OnStopped(bool _) => Unregister();

    void Unregister()
    {
        if (!registered) return;
        registered = false;
        var cm = nm.CustomMessagingManager;
        if (cm != null)
        {
            cm.UnregisterNamedMessageHandler(ProbeMsg);
            cm.UnregisterNamedMessageHandler(PosMsg);
        }
#if !NETPROBE_NO_BANDWIDTH
        if (nm.NetworkConfig != null && nm.NetworkConfig.NetworkTransport != null)
            nm.NetworkConfig.NetworkTransport.OnTransportEvent -= OnTransportEvent;
#endif
    }

    void Update()
    {
        if (!registered || nm == null || !nm.IsListening) return;
        float now = Time.unscaledTime;

        if (now >= nextProbe) { nextProbe = now + 1f / probeHz; SendProbe(); }

        if (nm.IsServer) RecordHistory();
        else if (nm.IsConnectedClient && now >= nextPos) { nextPos = now + 1f / posReportHz; SendPos(); }

        if (now - windowStart >= windowSeconds)
        {
            CloseWindow(now - windowStart);
            windowStart = now;
        }
    }

    // ---------------- Packet loss ----------------

    void SendProbe()
    {
        sendSeq++;
        using (var w = new FastBufferWriter(8, Allocator.Temp))
        {
            w.WriteValueSafe(sendSeq);
            var cm = nm.CustomMessagingManager;
            if (nm.IsServer)
            {
                foreach (ulong id in nm.ConnectedClientsIds)
                    if (id != NetworkManager.ServerClientId)
                        cm.SendNamedMessage(ProbeMsg, id, w, NetworkDelivery.Unreliable);
            }
            else
            {
                cm.SendNamedMessage(ProbeMsg, NetworkManager.ServerClientId, w, NetworkDelivery.Unreliable);
            }
        }
    }

    void OnProbe(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out uint seq);
        Rx s = GetRx(sender);
        if (!s.started)
        {
            s.started = true;
            s.prevMax = seq - 1;
            s.max = seq - 1;
        }
        s.count++;
        if (seq > s.max) s.max = seq;
    }

    Rx GetRx(ulong id)
    {
        if (!rx.TryGetValue(id, out Rx s)) { s = new Rx(); rx[id] = s; }
        return s;
    }

    void CloseWindow(float dt)
    {
        foreach (var kv in rx)
        {
            Rx s = kv.Value;
            if (s.started)
            {
                uint expected = s.max - s.prevMax;
                s.lossPct = expected > 0 ? Mathf.Max(0f, 100f * (1f - (float)s.count / expected)) : float.NaN;
                s.prevMax = s.max;
            }
            s.count = 0;
#if !NETPROBE_NO_BANDWIDTH
            s.inKBps = s.bytes / 1024f / Mathf.Max(0.001f, dt);
#endif
            s.bytes = 0;
        }
    }

    // ---------------- Bandwidth ----------------

#if !NETPROBE_NO_BANDWIDTH
    void OnTransportEvent(NetworkEvent evt, ulong clientId, ArraySegment<byte> payload, float receiveTime)
    {
        if (evt != NetworkEvent.Data) return;
        GetRx(clientId).bytes += payload.Count;
    }
#endif

    // ---------------- Position error ----------------

    void SendPos()
    {
        var po = nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
        if (po == null) return;
        using (var w = new FastBufferWriter(32, Allocator.Temp))
        {
            w.WriteValueSafe(nm.ServerTime.Time);
            w.WriteValueSafe(po.transform.position);
            nm.CustomMessagingManager.SendNamedMessage(PosMsg, NetworkManager.ServerClientId, w, NetworkDelivery.Unreliable);
        }
    }

    void RecordHistory()
    {
        double t = nm.ServerTime.Time;
        foreach (var kv in nm.ConnectedClients)
        {
            if (kv.Key == NetworkManager.ServerClientId) continue;
            var po = kv.Value.PlayerObject;
            if (po == null) continue;
            if (!history.TryGetValue(kv.Key, out List<Sample> h)) { h = new List<Sample>(256); history[kv.Key] = h; }
            h.Add(new Sample { t = t, p = po.transform.position });
            while (h.Count > 0 && t - h[0].t > historySeconds) h.RemoveAt(0);
        }
    }

    void OnPos(ulong sender, FastBufferReader reader)
    {
        if (!nm.IsServer) return;
        reader.ReadValueSafe(out double t);
        reader.ReadValueSafe(out Vector3 ownerPos);
        if (!history.TryGetValue(sender, out List<Sample> h) || h.Count < 2) return;
        if (t < h[0].t || t > h[h.Count - 1].t) return;   // ngoài khoảng lịch sử: bỏ qua

        for (int i = h.Count - 1; i > 0; i--)
        {
            if (h[i - 1].t > t) continue;
            double span = h[i].t - h[i - 1].t;
            float k = span > 1e-6 ? (float)((t - h[i - 1].t) / span) : 0f;
            Vector3 replica = Vector3.Lerp(h[i - 1].p, h[i].p, k);
            NetMetricsHub.RaisePosError(sender, Vector3.Distance(ownerPos, replica));
            return;
        }
    }
}
