namespace Fleet.Orchestration.Configuration;

/// <summary>Secret-store names referenced from config: lower-case letters, digits, dot, dash and underscore.</summary>
internal static class SecretName
{
    public static bool IsValid(string name) =>
        name.Length is > 0 and <= 128 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.') && name[0] != '.';
}
