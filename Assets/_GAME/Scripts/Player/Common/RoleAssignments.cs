using System.Collections.Generic;

/// <summary>Server ghi nhớ ai làm vai gì (chỉ dùng trên server/host, sống qua việc đổi scene).</summary>
public static class RoleAssignments
{
    static readonly Dictionary<ulong, Role> map = new Dictionary<ulong, Role>();
    public static void Clear() => map.Clear();
    public static void Set(ulong clientId, Role role) => map[clientId] = role;
    public static Role Get(ulong clientId) => map.TryGetValue(clientId, out Role r) ? r : Role.Survivor;
    public static bool Has(ulong clientId) => map.ContainsKey(clientId);     
}
