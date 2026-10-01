using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AlwaysOnTopper;

internal static class Tests
{
    [DllImport("user32.dll")] static extern void NotifyWinEvent(uint type, IntPtr hwnd, int obj, int child);
    [DllImport("user32.dll")] static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, Native.EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetMenuItemRect(IntPtr window, IntPtr menu, uint item, out Native.RECT rect);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    sealed class TestForm : Form { internal uint Selected { get; set; } internal int MenuPosition; protected override void WndProc(ref Message m) { if (m.Msg == 0x120) { m.Result = new IntPtr((2 << 16) | MenuPosition); return; } if (m.Msg == 0x112 && (m.WParam.ToInt64() & 0xFFF0) < 0xF000) Selected = (uint)(m.WParam.ToInt64() & 0xFFF0); base.WndProc(ref m); } }
    static string Read(string path) { var until=DateTime.UtcNow.AddSeconds(5); while (true) { try { return File.ReadAllText(path); } catch (IOException) { if (DateTime.UtcNow > until) throw; Thread.Sleep(10); } } }
    static int passed;
    static void Publish(string path, string value) { File.WriteAllText(path + ".tmp", value); File.Move(path + ".tmp", path); }
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); passed++;
    }
    static void Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new Exception("Timed out");
            Application.DoEvents(); Thread.Sleep(10);
        }
    }
    static bool Top(IntPtr hwnd)
    {
        Native.WINDOWINFO info;
        return Native.TryWindowInfo(hwnd, out info) && (info.dwExStyle & 8) != 0;
    }
    static void AddForeign(IntPtr menu)
    {
        var item = Native.MenuItem(Native.MIIM_ID | Native.MIIM_STRING);
        item.wID = 0x7A00; item.dwTypeData = "Foreign command";
        if (!Native.InsertMenuItem(menu, 0, true, ref item)) throw new Exception("Insert foreign");
    }
    [STAThread] static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "child")
        {
            using (var a = new TestForm()) using (var b = new Form())
            using (var timer = new System.Windows.Forms.Timer())
            {
                IntPtr ah = a.Handle, bh = b.Handle; a.Show();
                AddForeign(Native.GetSystemMenu(ah, false));
                Native.SetWindowPos(bh, new IntPtr(-1), 0, 0, 0, 0, Native.PositionFlags);
                Publish(args[1], ah.ToInt64() + "\n" + bh.ToInt64());
                timer.Interval = 20;
                timer.Tick += delegate
                {
                    if (!File.Exists(args[2])) return;
                    string command = Read(args[2]); File.Delete(args[2]);
                    if (command == "exit") Application.ExitThread();
                    else if (command == "reset") { Native.GetSystemMenu(ah, true); AddForeign(Native.GetSystemMenu(ah, false)); }
                    else if (command.StartsWith("invoke:")) NotifyWinEvent(0x8013, ah, -1, int.Parse(command.Substring(7)));
                    else if (command.StartsWith("menu:"))
                    {
                        uint requested = uint.Parse(command.Substring(5));
                        IntPtr menu = Native.GetSystemMenu(ah, false);
                        uint process; uint thread = Native.GetWindowThreadProcessId(ah, out process);
                        int position = 0;
                        while (position < Native.GetMenuItemCount(menu) &&
                            Native.GetMenuItemID(menu, (uint)position) != requested) position++;
                        // Deliver a menu character only to our test window, never to the user input focus.
                        var worker = new Thread(delegate()
                        {
                            Thread.Sleep(200);
                            EnumThreadWindows(thread, delegate(IntPtr w, IntPtr p)
                            {
                                var name = new System.Text.StringBuilder(32);
                                Native.GetClassName(w, name, 32);
                                if (name.ToString() != "#32768") return true;
                                PostMessage(ah, 0x102, new IntPtr(122), new IntPtr(1));
                                return true;
                            }, IntPtr.Zero);
                        });
                        worker.IsBackground = true; worker.Start();
                        a.Activate(); a.Focus(); a.MenuPosition = position; a.Selected = 0; SendMessage(ah, 0x112, new IntPtr(0xF100), new IntPtr(32)); uint selected = a.Selected;
                        a.BeginInvoke(new Action(delegate { Publish(args[2] + ".result", a.Selected.ToString()); }));
                    }
                };
                timer.Start(); Application.Run();
            }
            return 0;
        }
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string handles = Path.Combine(root, "handles.txt"), control = Path.Combine(root, "control.txt");
        if (File.Exists(handles)) File.Delete(handles);
        if (File.Exists(control)) File.Delete(control);
        if (File.Exists(control + ".result")) File.Delete(control + ".result");
        Process child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo {
                FileName = args.Length > 0 ? args[0] : System.Reflection.Assembly.GetExecutingAssembly().Location,
                Arguments = "child \"" + handles + "\" \"" + control + "\"", UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            Wait(() => File.Exists(handles));
            var lines = Read(handles).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            IntPtr a = new IntPtr(long.Parse(lines[0])), b = new IntPtr(long.Parse(lines[1]));
            using (var context = new TopperContext(false))
            {
                var ea = context.Attach(a); var eb = context.Attach(b);
                Check(ea != null && eb != null, "attach to two windows in another process");
                Check(ea.Command != 0x7A00, "avoid existing command ID collision");
                int count = Native.GetMenuItemCount(ea.Menu);
                Check(context.Attach(a) == ea && Native.GetMenuItemCount(ea.Menu) == count, "no duplicate menu item");
                Check(!context.HandleInvocation(a, -1, (int)ea.Command, ea.Thread + 1), "reject wrong event thread");
                Check(!context.HandleInvocation(a, -1, 123, ea.Thread), "reject another menu command");
                Check(!context.HandleInvocation(b, -1, (int)ea.Command, eb.Thread), "do not redirect event to another window");
                Check(context.HandleInvocation(a, -1, (int)ea.Command, ea.Thread), "first invocation accepted");
                Wait(() => Top(a)); context.Synchronize(ea);
                var state = Native.MenuItem(Native.MIIM_STATE);
                Native.GetMenuItemInfo(ea.Menu, ea.Command, false, ref state);
                Check((state.fState & 8) != 0, "menu checkmark follows real topmost state");
                AddForeign(ea.Menu);
                Check(context.HandleInvocation(a, -1, (int)ea.Command, ea.Thread), "second invocation after menu reordering");
                Wait(() => !Top(a)); context.Synchronize(ea);
                Check(!Top(a), "second invocation cancels topmost");
                Check(context.Toggle(eb), "toggle initially topmost window");
                Wait(() => !Top(b)); context.Synchronize(eb);
                Check(context.Toggle(ea), "toggle again before menu rebuild"); Wait(() => Top(a));
                Publish(control, "reset"); Wait(() => !File.Exists(control));
                var replacement = context.Attach(a);
                Check(replacement != null && replacement.OriginalTopmost == false,
                    "menu rebuild preserves original window state");
                // Exercise the real out-of-context WinEvent delivery and managed callback after GC.
                Native.WinEventProc callback = delegate(IntPtr h, uint e, IntPtr w, int o, int c, uint t, uint tm) {
                    if (e <= 7 || e == 0x8013) context.ProcessEvent(e, w, o, c, t, tm);
                };
                IntPtr menuHook = Native.SetWinEventHook(3, 7, IntPtr.Zero, callback, 0, 0, 0);
                IntPtr hook = Native.SetWinEventHook(0x8013, 0x8013, IntPtr.Zero, callback, 0, 0, 0);
                Check(hook != IntPtr.Zero, "register global out-of-context hook");
                try
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    context.Synchronize(replacement);
                    Publish(control, "invoke:" + replacement.Command);
                    Wait(() => !Top(a)); context.Synchronize(replacement);
                    Check(!Top(a), "cross-process WinEvent cancels topmost after GC");
                    Publish(control, "menu:" + replacement.Command);
                    Wait(() => File.Exists(control + ".result"));
                    uint selected = uint.Parse(Read(control + ".result"));
                    File.Delete(control + ".result");
                    Check(selected == replacement.Command, "real native system menu selects custom command");
                    Wait(() => Top(a)); context.Synchronize(replacement);
                    Check(Top(a), "real native menu generates usable INVOKED event");
                    Publish(control, "menu:" + replacement.Command);
                    Wait(() => File.Exists(control + ".result"));
                    selected = uint.Parse(Read(control + ".result"));
                    File.Delete(control + ".result");
                    Check(selected == replacement.Command, "real checked menu selects same command ID");
                    Wait(() => !Top(a)); context.Synchronize(replacement);
                    Check(!Top(a), "second real menu selection cancels topmost");
                    Check(context.Toggle(replacement), "request topmost before cleanup"); Wait(() => Top(a));
                }
                finally { Native.UnhookWinEvent(hook); Native.UnhookWinEvent(menuHook); GC.KeepAlive(callback); }
            }
            Wait(() => !Top(a) && Top(b));
            Check(!Top(a) && Top(b), "cleanup restores both original topmost states");
            var foreign = Native.MenuItem(Native.MIIM_ID);
            Check(Native.GetMenuItemInfo(Native.GetSystemMenu(a, false), 0x7A00, false, ref foreign),
                "cleanup preserves another application's custom command");
            Check(Native.GetMenuItemCount(Native.GetSystemMenu(a, false)) == 8,
                "cleanup removes only our inserted item");
            Console.WriteLine("All " + passed + " checks passed."); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally
        {
            if (child != null && !child.HasExited)
            {
                Publish(control, "exit");
                if (!child.WaitForExit(3000)) child.Kill();
                child.Dispose();
            }
        }
    }
}















