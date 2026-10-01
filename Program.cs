using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AlwaysOnTopper
{
    internal static class Program
    {
        private static readonly Dictionary<string, DateTime> lastLog = new Dictionary<string, DateTime>();
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new System.Threading.Mutex(true,
                @"Local\AlwaysOnTopper.SystemMenu.v2", out created))
            {
                if (!created) return;
                try
                {
                    Application.EnableVisualStyles();
                    using (var context = new TopperContext()) Application.Run(context);
                }
                catch (Exception ex)
                {
                    Log(ex.ToString());
                    MessageBox.Show(ex.Message, "Always on top", MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        internal static void Log(string message)
        {
            DateTime last;
            if (lastLog.TryGetValue(message, out last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(30))
                return;
            if (lastLog.Count > 100) lastLog.Clear();
            lastLog[message] = DateTime.UtcNow;
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "AlwaysOnTopper.log"), DateTime.Now.ToString("s") + " " + message +
                    Environment.NewLine);
            }
            catch { Debug.WriteLine(message); }
        }
    }

    internal sealed class TopperContext : ApplicationContext
    {
        private const string Caption = "Always on top";
        private readonly Dictionary<IntPtr, Entry> windows = new Dictionary<IntPtr, Entry>();
        private readonly Dictionary<uint, MenuSession> menus = new Dictionary<uint, MenuSession>();
        private readonly Queue<WindowEvent> events = new Queue<WindowEvent>();
        // Native hooks do not keep the managed delegate alive.
        private readonly Native.WinEventProc callback;
        private readonly Native.EnumWindowsProc enumerate;
        private readonly uint ownProcess = (uint)Process.GetCurrentProcess().Id;
        private readonly IntPtr marker = new IntPtr(Guid.NewGuid().GetHashCode() | 1);
        private readonly string propertyName = "AlwaysOnTopper." + Guid.NewGuid().ToString("N");
        private IntPtr invokedHook;
        private IntPtr foregroundHook;
        private Timer timer;
        private NotifyIcon tray;
        private ContextMenuStrip trayMenu;
        private ShutdownWindow shutdownWindow;
        private bool busy;
        private bool stopped;

        internal sealed class Entry
        {
            internal IntPtr Window, Menu;
            internal uint Process, Thread, Command;
            internal bool? OriginalTopmost;
            internal bool? Pending;
            internal DateTime Deadline;
            internal bool ReportedUpdateFailure;
        }

        private sealed class MenuSession
        {
            internal Entry Owner;
            internal IntPtr Popup;
            internal uint Started;
            internal uint? Ended;
            internal DateTime Expires;
        }

        private struct WindowEvent
        {
            internal uint Type, Thread, Timestamp;
            internal IntPtr Window;
            internal int ObjectId, ChildId;
        }

        internal TopperContext() : this(true) { }

        // The test harness can avoid registering desktop hooks or displaying a tray icon.
        internal TopperContext(bool start)
        {
            callback = OnEvent;
            enumerate = delegate(IntPtr hwnd, IntPtr unused)
            {
                try { if (Native.IsWindowVisible(hwnd)) Attach(hwnd); }
                catch (Exception ex) { Program.Log("Attach: " + ex); }
                return true;
            };
            if (!start) return;
            try
            {
                invokedHook = Native.SetWinEventHook(Native.EVENT_OBJECT_INVOKED,
                    Native.EVENT_OBJECT_INVOKED, IntPtr.Zero, callback, 0, 0, 0);
                if (invokedHook == IntPtr.Zero) throw new Win32Exception(
                    Marshal.GetLastWin32Error(), "Cannot register the menu event hook.");
                foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND,
                    Native.EVENT_SYSTEM_MENUPOPUPEND, IntPtr.Zero, callback, 0, 0, 0);
                if (foregroundHook == IntPtr.Zero) throw new Win32Exception(
                    Marshal.GetLastWin32Error(), "Cannot register the foreground event hook.");

                trayMenu = new ContextMenuStrip();
                trayMenu.Items.Add("Exit and restore windows", null, delegate { ExitThread(); });
                tray = new NotifyIcon { Icon = SystemIcons.Application, Text = Caption,
                    ContextMenuStrip = trayMenu, Visible = true };
                shutdownWindow = new ShutdownWindow(this);
                timer = new Timer { Interval = 500 };
                timer.Tick += delegate { RefreshWindows(); };
                RefreshWindows();
                timer.Start();
            }
            catch { Dispose(); throw; }
        }

        internal Entry Attach(IntPtr hwnd, bool includeOwnProcess = false)
        {
            if (stopped || hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return null;
            uint process;
            uint thread = Native.GetWindowThreadProcessId(hwnd, out process);
            if (thread == 0 || (!includeOwnProcess && process == ownProcess)) return null;
            Native.WINDOWINFO info;
            if (!Native.TryWindowInfo(hwnd, out info) ||
                (info.dwStyle & Native.WS_SYSMENU) == 0 ||
                (info.dwStyle & Native.WS_CHILD) != 0) return null;

            Entry entry;
            Entry previous = null;
            if (windows.TryGetValue(hwnd, out entry))
            {
                if (SameWindow(entry))
                {
                    if (OwnItem(entry)) return entry;
                    previous = entry; // Preserve the original state when the application rebuilds its menu.
                }
                else windows.Remove(hwnd); // A destroyed HWND may be reused by another process/thread.
            }
            IntPtr menu = Native.GetSystemMenu(hwnd, false);
            int count = Native.GetMenuItemCount(menu);
            if (menu == IntPtr.Zero || count < 0) return null;

            uint command;
            var used = new HashSet<uint>();
            if (!CollectCommands(menu, used, new HashSet<IntPtr>())) return null;
            for (command = 0x7A00; command < 0xF000 && used.Contains(command); command += 0x10) { }
            if (command >= 0xF000) return null;
            if (!Native.SetProp(hwnd, propertyName, marker))
            {
                Program.Log("SetProp failed: " + Marshal.GetLastWin32Error());
                if (previous != null) windows[hwnd] = previous;
                return null;
            }
            var item = Native.MenuItem(Native.MIIM_ID | Native.MIIM_STRING |
                Native.MIIM_STATE | Native.MIIM_DATA);
            item.wID = command;
            item.dwTypeData = Caption;
            item.dwItemData = marker;
            item.fState = (info.dwExStyle & Native.WS_EX_TOPMOST) != 0 ? Native.MFS_CHECKED : 0;
            // Insert before Close, or append if the target has removed its Close command.
            uint position = (uint)count;
            for (uint i = 0; i < count; i++)
                if (Native.GetMenuItemID(menu, i) == Native.SC_CLOSE) { position = i; break; }
            if (!Native.InsertMenuItem(menu, position, true, ref item))
            {
                Program.Log("InsertMenuItem failed: " + Marshal.GetLastWin32Error());
                if (previous != null) windows[hwnd] = previous;
                else Native.RemoveProp(hwnd, propertyName);
                return null;
            }
            entry = new Entry { Window = hwnd, Menu = menu, Process = process,
                Thread = thread, Command = command };
            if (previous != null)
            {
                entry.OriginalTopmost = previous.OriginalTopmost;
                entry.Pending = previous.Pending;
                entry.Deadline = previous.Deadline;
            }
            windows[hwnd] = entry;
            Native.DrawMenuBar(hwnd);
            return entry;
        }

        private static bool CollectCommands(IntPtr menu, HashSet<uint> used, HashSet<IntPtr> visited)
        {
            if (!visited.Add(menu)) return true;
            int count = Native.GetMenuItemCount(menu);
            if (count < 0) return false;
            for (uint i = 0; i < count; i++)
            {
                var item = Native.MenuItem(Native.MIIM_ID | Native.MIIM_SUBMENU);
                if (!Native.GetMenuItemInfo(menu, i, true, ref item)) return false;
                // WM_SYSCOMMAND reserves the low four bits. Avoid masked collisions too.
                used.Add(item.wID & 0xFFF0);
                if (item.hSubMenu != IntPtr.Zero && !CollectCommands(item.hSubMenu, used, visited))
                    return false;
            }
            return true;
        }

        private bool SameWindow(Entry entry)
        {
            uint process;
            uint thread = Native.GetWindowThreadProcessId(entry.Window, out process);
            return thread != 0 && thread == entry.Thread && process == entry.Process &&
                Native.GetProp(entry.Window, propertyName) == marker;
        }

        internal bool OwnItem(Entry entry)
        {
            if (!SameWindow(entry) || Native.GetSystemMenu(entry.Window, false) != entry.Menu)
                return false;
            var item = Native.MenuItem(Native.MIIM_DATA | Native.MIIM_ID);
            return Native.GetMenuItemInfo(entry.Menu, entry.Command, false, ref item) &&
                item.wID == entry.Command && item.dwItemData == marker;
        }

        private void RefreshWindows()
        {
            if (stopped || busy) return;
            busy = true;
            try
            {
                foreach (var entry in windows.Values.ToArray())
                {
                    if (!SameWindow(entry)) { windows.Remove(entry.Window); continue; }
                    Synchronize(entry);
                }
                foreach (var pair in menus.ToArray())
                    if (!SameWindow(pair.Value.Owner) ||
                        (pair.Value.Ended.HasValue && DateTime.UtcNow >= pair.Value.Expires))
                        menus.Remove(pair.Key);
                // Also catches new windows and menus rebuilt by their owning application.
                if (!Native.EnumWindows(enumerate, IntPtr.Zero))
                    Program.Log("EnumWindows failed: " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { Program.Log("Refresh: " + ex); }
            finally { busy = false; DrainEvents(); }
        }

        private void OnEvent(IntPtr hook, uint type, IntPtr hwnd, int objectId,
            int childId, uint thread, uint timestamp)
        {
            ProcessEvent(type, hwnd, objectId, childId, thread, timestamp);
        }

        internal void ProcessEvent(uint type, IntPtr hwnd, int objectId,
            int childId, uint thread, uint timestamp)
        {
            // Never allow managed exceptions to escape a native callback.
            if (stopped) return;
            events.Enqueue(new WindowEvent { Type = type, Window = hwnd,
                ObjectId = objectId, ChildId = childId, Thread = thread, Timestamp = timestamp });
            DrainEvents();
        }

        private void DrainEvents()
        {
            if (stopped || busy) return;
            busy = true;
            try
            {
                while (events.Count > 0)
                {
                    var e = events.Dequeue();
                    try { HandleEvent(e.Type, e.Window, e.ObjectId, e.ChildId, e.Thread, e.Timestamp); }
                    catch (Exception ex) { Program.Log("WinEvent: " + ex); }
                }
            }
            finally { busy = false; }
        }

        private void HandleEvent(uint type, IntPtr hwnd, int objectId,
            int childId, uint thread, uint timestamp)
        {
            if (type == Native.EVENT_SYSTEM_FOREGROUND) { Attach(hwnd); return; }
            MenuSession session;
            if (type == Native.EVENT_SYSTEM_MENUSTART)
            {
                // MENUSTART has the durable owner HWND; INVOKED may have a destroyed popup HWND.
                var owner = objectId == -1 ? Attach(hwnd) : null;
                if (owner == null || owner.Thread != thread) menus.Remove(thread);
                else menus[thread] = new MenuSession { Owner = owner, Started = timestamp };
                return;
            }
            if (type == Native.EVENT_SYSTEM_MENUPOPUPSTART)
            {
                if (menus.TryGetValue(thread, out session) && !session.Ended.HasValue &&
                    session.Popup == IntPtr.Zero) session.Popup = hwnd;
                return;
            }
            if (type == Native.EVENT_SYSTEM_MENUEND)
            {
                if (menus.TryGetValue(thread, out session))
                {
                    session.Ended = timestamp;
                    session.Expires = DateTime.UtcNow.AddSeconds(2);
                }
                return;
            }
            if (type != Native.EVENT_OBJECT_INVOKED) return;
            HandleInvocation(hwnd, objectId, childId, thread, timestamp);
        }

        internal bool HandleInvocation(IntPtr hwnd, int objectId, int childId, uint thread,
            uint timestamp = 0)
        {
            if (stopped || childId <= 0 || thread == 0) return false;
            Entry entry;
            // Standard system-menu INVOKED events supply the command ID as idChild.
            // Match the event owner, never the current foreground window.
            if (!windows.TryGetValue(hwnd, out entry))
            {
                MenuSession session;
                if (!menus.TryGetValue(thread, out session) || session.Popup != hwnd ||
                    unchecked((int)(timestamp - session.Started)) < 0 ||
                    (session.Ended.HasValue &&
                        unchecked((int)(timestamp - session.Ended.Value)) > 0)) return false;
                entry = session.Owner;
            }
            if (entry.Thread != thread || (uint)childId != entry.Command ||
                (objectId != -1 && objectId != -3 && objectId != 0) || !OwnItem(entry))
                return false;
            return Toggle(entry);
        }

        internal bool Toggle(Entry entry)
        {
            if (!OwnItem(entry)) return false;
            Synchronize(entry);
            if (entry.Pending.HasValue) return false; // A previous asynchronous request is in flight.
            Native.WINDOWINFO info;
            if (!Native.TryWindowInfo(entry.Window, out info))
            {
                Program.Log("GetWindowInfo failed: " + Marshal.GetLastWin32Error());
                return false;
            }
            bool current = (info.dwExStyle & Native.WS_EX_TOPMOST) != 0;
            if (!Native.SetWindowPos(entry.Window, new IntPtr(current ? -2 : -1),
                0, 0, 0, 0, Native.PositionFlags))
            {
                Program.Log("SetWindowPos failed: " + Marshal.GetLastWin32Error());
                return false;
            }
            if (!entry.OriginalTopmost.HasValue) entry.OriginalTopmost = current;
            entry.Pending = !current;
            entry.Deadline = DateTime.UtcNow.AddSeconds(3);
            Synchronize(entry);
            return true;
        }

        internal void Synchronize(Entry entry)
        {
            Native.WINDOWINFO info;
            if (!SameWindow(entry) || !Native.TryWindowInfo(entry.Window, out info)) return;
            bool topmost = (info.dwExStyle & Native.WS_EX_TOPMOST) != 0;
            if (entry.Pending.HasValue)
            {
                if (topmost == entry.Pending.Value) entry.Pending = null;
                else if (DateTime.UtcNow >= entry.Deadline)
                {
                    entry.Pending = null;
                    Program.Log("Window did not keep the requested topmost state; " +
                        "it may be unresponsive or enforcing its own window order.");
                }
            }
            if (!OwnItem(entry)) return;
            var state = Native.MenuItem(Native.MIIM_STATE);
            if (!Native.GetMenuItemInfo(entry.Menu, entry.Command, false, ref state)) return;
            uint desired = topmost ? state.fState | Native.MFS_CHECKED :
                state.fState & ~Native.MFS_CHECKED;
            if (state.fState == desired) return;
            state.fState = desired;
            if (!Native.SetMenuItemInfo(entry.Menu, entry.Command, false, ref state))
            {
                int error = Marshal.GetLastWin32Error();
                if (!entry.ReportedUpdateFailure) Program.Log("SetMenuItemInfo failed: " + error);
                entry.ReportedUpdateFailure = true;
            }
            else { entry.ReportedUpdateFailure = false; Native.DrawMenuBar(entry.Window); }
        }

        protected override void ExitThreadCore()
        {
            Cleanup();
            base.ExitThreadCore();
        }

        private sealed class ShutdownWindow : NativeWindow
        {
            private readonly TopperContext context;
            internal ShutdownWindow(TopperContext owner)
            {
                context = owner;
                CreateHandle(new CreateParams { Caption = "AlwaysOnTopper shutdown listener" });
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x11) { message.Result = new IntPtr(1); return; }
                if (message.Msg == 0x16 && message.WParam != IntPtr.Zero)
                {
                    context.ExitThread();
                    return;
                }
                base.WndProc(ref message);
            }
        }

        private void Cleanup()
        {
            if (stopped) return;
            stopped = true;
            if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
            foreach (IntPtr hook in new[] { invokedHook, foregroundHook })
                if (hook != IntPtr.Zero && !Native.UnhookWinEvent(hook))
                    Program.Log("UnhookWinEvent failed: " + Marshal.GetLastWin32Error());
            invokedHook = foregroundHook = IntPtr.Zero;
            foreach (var entry in windows.Values.ToArray())
            {
                if (!SameWindow(entry)) continue;
                if (entry.OriginalTopmost.HasValue &&
                    !Native.SetWindowPos(entry.Window,
                        new IntPtr(entry.OriginalTopmost.Value ? -1 : -2),
                        0, 0, 0, 0, Native.PositionFlags))
                    Program.Log("Restore SetWindowPos failed: " + Marshal.GetLastWin32Error());
                if (OwnItem(entry))
                {
                    // Remove only our entry; resetting the entire system menu destroys others' items.
                    if (!Native.RemoveMenu(entry.Menu, entry.Command, 0))
                        Program.Log("RemoveMenu failed: " + Marshal.GetLastWin32Error());
                    Native.DrawMenuBar(entry.Window);
                }
                Native.RemoveProp(entry.Window, propertyName);
            }
            windows.Clear();
            menus.Clear();
            events.Clear();
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            if (trayMenu != null) { trayMenu.Dispose(); trayMenu = null; }
            if (shutdownWindow != null) { shutdownWindow.DestroyHandle(); shutdownWindow = null; }
            GC.KeepAlive(callback);
            GC.KeepAlive(enumerate);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Cleanup();
            base.Dispose(disposing);
        }
    }

    internal static class Native
    {
        internal const uint EVENT_OBJECT_INVOKED = 0x8013, EVENT_SYSTEM_FOREGROUND = 3,
            EVENT_SYSTEM_MENUSTART = 4, EVENT_SYSTEM_MENUEND = 5,
            EVENT_SYSTEM_MENUPOPUPSTART = 6, EVENT_SYSTEM_MENUPOPUPEND = 7;
        internal const uint WS_EX_TOPMOST = 8, WS_SYSMENU = 0x80000, WS_CHILD = 0x40000000;
        internal const uint MIIM_STATE = 1, MIIM_ID = 2, MIIM_SUBMENU = 4, MIIM_DATA = 0x20, MIIM_STRING = 0x40;
        internal const uint MFS_CHECKED = 8, SC_CLOSE = 0xF060;
        // NOMOVE | NOSIZE | NOACTIVATE | NOOWNERZORDER | ASYNCWINDOWPOS. No NOZORDER!
        internal const uint PositionFlags = 0x0002 | 0x0001 | 0x0010 | 0x0200 | 0x4000;
        internal delegate void WinEventProc(IntPtr hook, uint type, IntPtr hwnd,
            int objectId, int childId, uint thread, uint timestamp);
        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct WINDOWINFO
        {
            internal uint cbSize;
            internal RECT rcWindow, rcClient;
            internal uint dwStyle, dwExStyle, dwWindowStatus, cxWindowBorders, cyWindowBorders;
            internal ushort atomWindowType, wCreatorVersion;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MENUITEMINFO
        {
            internal uint cbSize, fMask, fType, fState, wID;
            internal IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
            [MarshalAs(UnmanagedType.LPWStr)] internal string dwTypeData;
            internal uint cch;
            internal IntPtr hbmpItem;
        }
        internal static MENUITEMINFO MenuItem(uint mask)
        {
            return new MENUITEMINFO { cbSize = (uint)Marshal.SizeOf(typeof(MENUITEMINFO)), fMask = mask };
        }
        internal static bool TryWindowInfo(IntPtr hwnd, out WINDOWINFO info)
        {
            info = new WINDOWINFO { cbSize = (uint)Marshal.SizeOf(typeof(WINDOWINFO)) };
            return GetWindowInfo(hwnd, ref info);
        }
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowInfo(IntPtr hwnd, ref WINDOWINFO info);
        [DllImport("user32.dll")] internal static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int GetMenuItemCount(IntPtr menu);
        [DllImport("user32.dll")] internal static extern uint GetMenuItemID(IntPtr menu, uint position);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool InsertMenuItem(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RemoveMenu(IntPtr menu, uint item, uint flags);
        [DllImport("user32.dll")] internal static extern bool DrawMenuBar(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetProp(IntPtr hwnd, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr RemoveProp(IntPtr hwnd, string name);
    }
}

