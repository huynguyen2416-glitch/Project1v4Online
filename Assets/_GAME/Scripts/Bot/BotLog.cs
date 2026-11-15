using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Ghi nhật ký bot ra CSV (làm minh chứng cho báo cáo): bot_log.csv trong Application.persistentDataPath.
/// Cột: time (giây từ lúc chạy game), wall_clock, who, event, detail. Mỗi lần đổi trạng thái FSM hoặc đổi mục tiêu là một dòng.
/// </summary>
public static class BotLog
{
    static StreamWriter writer;
    static bool failed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset()
    {
        writer = null;
        failed = false;
    }

    public static string FilePath => Path.Combine(Application.persistentDataPath, "bot_log.csv");

    public static void Write(string who, string evt, string detail)
    {
        if (failed) return;
        try
        {
            if (writer == null)
            {
                bool isNew = !File.Exists(FilePath);
                writer = new StreamWriter(FilePath, true, new UTF8Encoding(true)) { AutoFlush = true };
                if (isNew) writer.WriteLine("time,wall_clock,who,event,detail");
                Debug.Log("[Bot] Nhật ký: " + FilePath);
            }
            writer.WriteLine($"{Time.time:F2},{System.DateTime.Now:HH:mm:ss.fff},{Esc(who)},{Esc(evt)},{Esc(detail)}");
        }
        catch (System.Exception e)
        {
            failed = true;
            Debug.LogWarning("[Bot] Không ghi được nhật ký: " + e.Message);
        }
    }

    static string Esc(string s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
}
