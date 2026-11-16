using System.Runtime.InteropServices;

namespace NpuChat;

internal sealed class LanTrayIcon(nint window, string iconPath) : IDisposable
{
    internal const uint CallbackMessage = 0x830D; // WM_APP + 0x30D
    internal const int OpenCommand = 1;
    internal const int QuitCommand = 2;
    internal static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private const uint IconId = 1;
    private readonly nint icon = LoadImage(0, iconPath, 1, 0, 0, 0x0050); // IMAGE_ICON, LR_LOADFROMFILE | LR_DEFAULTSIZE
    private bool registered;

    internal bool Show()
    {
        if (icon == 0) return false;
        var data = IconData();
        data.Flags = 0x87; // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP
        data.Callback = CallbackMessage;
        data.Icon = icon;
        data.Tooltip = "Phoenix AI · LAN access running";
        if (!Shell_NotifyIconW(0, ref data)) return false; // NIM_ADD
        registered = true;
        data.Version = 4;
        if (Shell_NotifyIconW(4, ref data)) return true; // NIM_SETVERSION
        Remove();
        return false;
    }

    internal int Command(nuint position, nint notification)
    {
        uint value = unchecked((uint)notification);
        if (value >> 16 != IconId) return 0;
        return (value & 0xFFFF) switch
        {
            0x0400 or 0x0401 or 0x0202 => OpenCommand, // NIN_SELECT, NIN_KEYSELECT, WM_LBUTTONUP
            0x007B => ShowMenu(position), // WM_CONTEXTMENU
            _ => 0
        };
    }

    private int ShowMenu(nuint position)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return 0;
        try
        {
            if (!AppendMenu(menu, 0, OpenCommand, "Open Phoenix AI") ||
                !AppendMenu(menu, 0, QuitCommand, "Quit Phoenix AI")) return 0;
            SetForegroundWindow(window);
            int x = unchecked((short)position);
            int y = unchecked((short)(position >> 16));
            int command = TrackPopupMenu(menu, 0x0182, x, y, 0, window, 0); // TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD
            PostMessage(window, 0, 0, 0); // WM_NULL lets Explorer dismiss the menu cleanly.
            if (command == 0)
            {
                var data = IconData();
                Shell_NotifyIconW(3, ref data); // NIM_SETFOCUS
            }
            return command;
        }
        finally { DestroyMenu(menu); }
    }

    private NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = IconId,
        Tooltip = "", Info = "", InfoTitle = ""
    };

    private void Remove()
    {
        if (!registered) return;
        var data = IconData();
        Shell_NotifyIconW(2, ref data); // NIM_DELETE
        registered = false;
    }

    public void Dispose()
    {
        Remove();
        if (icon != 0) DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal uint Flags;
        internal uint Callback;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tooltip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid GuidItem;
        internal nint BalloonIcon;
    }

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint command, string label);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
