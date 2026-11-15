using System;
using System.IO;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>
/// Giả lập mạng E0-E3 (câu 4). Gắn vào cùng GameObject với NetworkManager và UnityTransport.
/// Chọn mức bằng dòng lệnh "-netlevel E2" (build) hoặc ô Editor Level (trong Editor). Chạy trong Awake, tức là TRƯỚC khi bấm
/// Host/Client, vì UnityTransport chỉ nhận tham số giả lập trước khi khởi động.
/// Khuyến nghị: chỉ đặt mức E1-E3 cho tiến trình CLIENT, host chạy E0. Mỗi lần chạy ghi một manifest_*.json (cấu hình để tái lập).
/// Tham số dòng lệnh khác: "-run nhan_lan_chay", "-commit ma_commit_git" (chỉ để ghi vào manifest).
/// SetDebugSimulatorParameters chỉ tồn tại trong Editor và Development Build; build thường thì mức E1-E3 không có tác dụng.
/// </summary>
[DisallowMultipleComponent]
public class NetSimulator : MonoBehaviour
{
    public enum Level { E0, E1, E2, E3 }

    [Serializable]
    public struct Profile
    {
        public int delayMs;
        public int jitterMs;
        [Range(0, 100)] public int dropPercent;
    }

    [Header("Mức dùng khi chạy trong Editor (build dùng -netlevel)")]
    [SerializeField] Level editorLevel = Level.E0;

    [Header("Thông số từng mức (đề xuất, sửa cho khớp với mức nhóm đã chọn)")]
    [SerializeField]
    Profile[] profiles =
    {
        new Profile { delayMs = 0,   jitterMs = 0,  dropPercent = 0 },   // E0
        new Profile { delayMs = 50,  jitterMs = 10, dropPercent = 0 },   // E1
        new Profile { delayMs = 100, jitterMs = 30, dropPercent = 2 },   // E2
        new Profile { delayMs = 200, jitterMs = 60, dropPercent = 5 },   // E3
    };

    /// <summary>Nhãn ghi vào CSV (không có dấu phẩy), vd "E2 delay=100 jitter=30 drop=2".</summary>
    public static string Label { get; private set; } = "none";

    [Serializable]
    class Manifest
    {
        public string level;
        public int delayMs, jitterMs, dropPercent;
        public bool simulatorActive;
        public string run, commit;
        public string startedAtLocal;
        public int processId;
        public string unityVersion, platform, operatingSystem, device, cpu;
        public string netcodeAssemblyVersion;
        public bool developmentBuild;
    }

    void Awake()
    {
        var utp = GetComponent<UnityTransport>();
        if (utp == null) { Debug.LogError("[NetSim] Không thấy UnityTransport cùng GameObject"); return; }

        Level level = ReadLevel(out string run, out string commit);
        int idx = (int)level;
        if (profiles == null || idx >= profiles.Length) { Debug.LogError("[NetSim] Thiếu profile cho mức " + level); return; }
        Profile p = profiles[idx];

        bool active = false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        utp.SetDebugSimulatorParameters(p.delayMs, p.jitterMs, p.dropPercent);
        active = true;
#else
        if (level != Level.E0)
            Debug.LogWarning("[NetSim] Build thường không có bộ giả lập mạng. Hãy dùng Development Build.");
#endif

        Label = $"{level} delay={p.delayMs} jitter={p.jitterMs} drop={p.dropPercent}";
        Debug.Log($"[NetSim] {Label} (simulatorActive={active})");
        WriteManifest(level, p, active, run, commit);
    }

    Level ReadLevel(out string run, out string commit)
    {
        Level level = editorLevel;
        run = "";
        commit = "";
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-netlevel")
            {
                if (Enum.TryParse(args[i + 1], true, out Level parsed)) level = parsed;
                else Debug.LogWarning("[NetSim] -netlevel không hợp lệ: " + args[i + 1] + " (dùng E0, E1, E2 hoặc E3)");
            }
            else if (args[i] == "-run") run = args[i + 1];
            else if (args[i] == "-commit") commit = args[i + 1];
        }
        return level;
    }

    void WriteManifest(Level level, Profile p, bool active, string run, string commit)
    {
        try
        {
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var m = new Manifest
            {
                level = level.ToString(),
                delayMs = p.delayMs,
                jitterMs = p.jitterMs,
                dropPercent = p.dropPercent,
                simulatorActive = active,
                run = run,
                commit = commit,
                startedAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                processId = pid,
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                operatingSystem = SystemInfo.operatingSystem,
                device = SystemInfo.deviceModel,
                cpu = SystemInfo.processorType + " x" + SystemInfo.processorCount,
                netcodeAssemblyVersion = typeof(NetworkManager).Assembly.GetName().Version.ToString(),
                developmentBuild = Debug.isDebugBuild,
            };
            string path = Path.Combine(Application.persistentDataPath,
                $"manifest_{DateTime.Now:yyyyMMdd_HHmmss}_p{pid}.json");
            File.WriteAllText(path, JsonUtility.ToJson(m, true));
            Debug.Log("[NetSim] Manifest: " + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[NetSim] Không ghi được manifest: " + e.Message);
        }
    }
}
