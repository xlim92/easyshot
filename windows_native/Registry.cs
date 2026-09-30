using Windows.Win32.System.Registry;

namespace EasyShot;

/// Values under HKEY_CURRENT_USER.
static unsafe class Registry
{
    public static uint? ReadNumber(string key, string name)
    {
        uint value, size = sizeof(uint);
        fixed (char* k = key, n = name)
            return PInvoke.RegGetValue(HKEY.HKEY_CURRENT_USER, k, n, REG_ROUTINE_FLAGS.RRF_RT_REG_DWORD, null, &value, &size) == WIN32_ERROR.NO_ERROR ? value : null;
    }

    public static string? ReadText(string key, string name)
    {
        const int length = 1024;
        var buffer = stackalloc char[length];
        uint size = length * sizeof(char);
        fixed (char* k = key, n = name)
            return PInvoke.RegGetValue(HKEY.HKEY_CURRENT_USER, k, n, REG_ROUTINE_FLAGS.RRF_RT_REG_SZ, null, buffer, &size) == WIN32_ERROR.NO_ERROR ? new string(buffer) : null;
    }

    public static void Write(string key, string name, uint value)
    {
        fixed (char* k = key, n = name)
            PInvoke.RegSetKeyValue(HKEY.HKEY_CURRENT_USER, k, n, (uint)REG_VALUE_TYPE.REG_DWORD, &value, sizeof(uint));
    }

    public static void Write(string key, string name, string value)
    {
        fixed (char* k = key, n = name, v = value)
            PInvoke.RegSetKeyValue(HKEY.HKEY_CURRENT_USER, k, n, (uint)REG_VALUE_TYPE.REG_SZ, v, (uint)(value.Length + 1) * sizeof(char));
    }
}
