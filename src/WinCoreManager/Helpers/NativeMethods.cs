using System.Runtime.InteropServices;
using System.Text;

namespace WinCoreManager.Helpers;

public static class NativeMethods
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    /// <summary>
    /// Página de código OEM do sistema (usada por sfc, dism, chkdsk e ipconfig
    /// quando a saída é redirecionada). Evita mojibake em textos acentuados.
    /// </summary>
    public static Encoding OemEncoding => Encoding.GetEncoding((int)GetOEMCP());

    public static Encoding AnsiEncoding => Encoding.GetEncoding((int)GetACP());
}
