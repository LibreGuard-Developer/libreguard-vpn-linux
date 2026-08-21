using System.Runtime.InteropServices;

namespace Avalonia.Controls.Win;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct EventRegistrationToken
{
    public long value;
}
