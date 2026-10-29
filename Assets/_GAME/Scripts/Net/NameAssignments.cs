using System.Collections.Generic;

public static class NameAssignments
{
    static readonly Dictionary<ulong, string> map = new Dictionary<ulong, string>();
    public static void Set(ulong id, string name) =>
        map[id] = string.IsNullOrWhiteSpace(name) ? Fallback(id) : name;
    public static string Get(ulong id) => map.TryGetValue(id, out string n) ? n : Fallback(id);
    static string Fallback(ulong id) => "Người chơi " + (id + 1);
}