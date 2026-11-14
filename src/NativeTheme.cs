using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

// Process-local native theming; never changes the user's Windows theme settings.
internal static class NativeTheme
{
    static bool initialized;
    [DllImport("uxtheme.dll",EntryPoint="#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll",EntryPoint="#133")] static extern bool AllowDarkModeForWindow(IntPtr window,bool allow);
    [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)] static extern int SetWindowTheme(IntPtr window,string app,string id);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr window,int index);
    [StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
    internal static void Initialize()
    {
        if(initialized)return;initialized=true;
        // Ordinal135 has this signature starting in Windows10 1903. Older systems
        // retain normal OS rendering rather than invoking an incompatible export.
        try{using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")){int build;if(key!=null&&Int32.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber")),out build)&&build>=18362)SetPreferredAppMode(2);}}catch{}
    }
    internal static void Attach(Control root)
    {
        Initialize();
        root.HandleCreated+=delegate{Apply(root);};
        if(root.IsHandleCreated)Apply(root);
        root.ControlAdded+=delegate(object sender,ControlEventArgs args){Attach(args.Control);};
        foreach(Control child in root.Controls)Attach(child);
    }
    static void Apply(Control control)
    {
        try
        {
            AllowDarkModeForWindow(control.Handle,true);
            SetWindowTheme(control.Handle,"DarkMode_Explorer",null);
            var list=control as ListView;
            if(list!=null){IntPtr header=SendMessage(list.Handle,0x101f,IntPtr.Zero,IntPtr.Zero);if(header!=IntPtr.Zero){SetWindowTheme(header,"DarkMode_Explorer",null);var painter=new HeaderTail(list);painter.AssignHandle(header);var frame=new ScrollCorner(list,header);frame.AssignHandle(list.Handle);list.Disposed+=delegate{painter.ReleaseHandle();frame.ReleaseHandle();};}}
            control.Invalidate(true);
        }
        catch(EntryPointNotFoundException){}catch(DllNotFoundException){}
    }
    sealed class ScrollCorner:NativeWindow
    {
        readonly ListView owner;readonly IntPtr header;
        internal ScrollCorner(ListView list,IntPtr h){owner=list;header=h;}
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if((message.Msg!=0x000f&&message.Msg!=0x0085)||owner.IsDisposed||(GetWindowLong(Handle,-16)&0x00200000)==0)return;
            Rect window,top;if(!GetWindowRect(Handle,out window)||!GetClientRect(header,out top))return;
            int width=window.Right-window.Left,bar=SystemInformation.VerticalScrollBarWidth,height=top.Bottom;
            IntPtr dc=GetWindowDC(Handle);if(dc==IntPtr.Zero)return;
            try{using(var graphics=Graphics.FromHdc(dc))using(var brush=new SolidBrush(Color.FromArgb(49,69,80)))graphics.FillRectangle(brush,width-bar,0,bar,height);}finally{ReleaseDC(Handle,dc);}
        }
    }
    sealed class HeaderTail:NativeWindow
    {
        readonly ListView owner;
        internal HeaderTail(ListView list){owner=list;}
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if((message.Msg!=0x000f&&message.Msg!=0x0317&&message.Msg!=0x0318)||owner.IsDisposed)return;
            // OwnerDraw paints column cells; Windows still paints unused header
            // space itself. Fill that tail to remove the white square beside it.
            Rect client;if(!GetClientRect(Handle,out client))return;
            int end=0;foreach(ColumnHeader column in owner.Columns)end+=column.Width;
            if(end>=client.Right)return;
            using(var graphics=message.Msg==0x000f?Graphics.FromHwnd(Handle):Graphics.FromHdc(message.WParam))using(var brush=new SolidBrush(Color.FromArgb(49,69,80)))graphics.FillRectangle(brush,end,0,client.Right-end,client.Bottom);
        }
    }
}
