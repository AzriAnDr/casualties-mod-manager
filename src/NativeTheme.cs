using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

// Process-local native theming; never changes the user's Windows theme settings.
internal static class NativeTheme
{
    static bool initialized;
    internal static void DrawColumnSeparator(Graphics graphics,Rectangle bounds,bool header)
    {
        if(bounds.Width<=0||bounds.Height<=0)return;
        using(var pen=new Pen(header?Color.FromArgb(71,91,102):Color.FromArgb(49,69,80)))
            graphics.DrawLine(pen,bounds.Right-1,bounds.Top,bounds.Right-1,bounds.Bottom-1);
    }
    internal static void DrawDetailsItem(ListView owner,DrawListViewItemEventArgs e,Color selectedColor,Color normalColor,Color textColor,Color? statusColor=null,int inset=6)
    {
        if(owner.View!=View.Details){e.DrawDefault=true;return;}
        e.DrawDefault=false;
        int offset=owner.IsHandleCreated?GetScrollPos(owner.Handle,0):0;
        var widths=new int[owner.Columns.Count];for(int i=0;i<widths.Length;i++)widths[i]=owner.Columns[i].Width;
        DrawDetailsRow(e.Graphics,e.Item,e.Bounds,owner.ClientRectangle,widths,offset,owner.Font,e.Item.Selected,selectedColor,normalColor,textColor,statusColor,inset);
    }
    internal static void DrawDetailsSubItem(ListView owner,DrawListViewSubItemEventArgs e,Color selectedColor,Color normalColor,Color textColor,Color? statusColor=null,int inset=6)
    {
        if(owner.View!=View.Details){e.DrawDefault=true;return;}
        e.DrawDefault=false;int x=owner.IsHandleCreated?-GetScrollPos(owner.Handle,0):0;
        for(int i=0;i<e.ColumnIndex;i++)x+=owner.Columns[i].Width;
        if(e.ColumnIndex<0||e.ColumnIndex>=owner.Columns.Count)return;
        var bounds=new Rectangle(x,e.Bounds.Top,owner.Columns[e.ColumnIndex].Width,e.Bounds.Height);
        PaintDetailsCell(e.Graphics,e.SubItem.Text,bounds,owner.ClientRectangle,owner.Font,e.Item.Selected?selectedColor:normalColor,e.ColumnIndex==1&&!e.Item.Selected&&statusColor.HasValue?statusColor.Value:textColor,inset);
    }
    internal static void DrawDetailsRow(Graphics graphics,ListViewItem item,Rectangle bounds,Rectangle client,int[] widths,int scrollOffset,Font font,bool selected,Color selectedColor,Color normalColor,Color textColor,Color? statusColor,int inset)
    {
        Rectangle clip=Rectangle.Intersect(bounds,client);if(clip.Width<=0||clip.Height<=0)return;
        var saved=graphics.Save();
        try
        {
            graphics.SetClip(clip,System.Drawing.Drawing2D.CombineMode.Intersect);
            using(var brush=new SolidBrush(selected?selectedColor:normalColor))graphics.FillRectangle(brush,clip);
            int x=-scrollOffset;
            for(int i=0;i<widths.Length;i++)
            {
                var cell=new Rectangle(x,bounds.Top,widths[i],bounds.Height);x+=widths[i];
                PaintDetailsCell(graphics,i<item.SubItems.Count?item.SubItems[i].Text:"",cell,clip,font,selected?selectedColor:normalColor,i==1&&!selected&&statusColor.HasValue?statusColor.Value:textColor,inset);
            }
        }
        finally{graphics.Restore(saved);}
    }
    static void PaintDetailsCell(Graphics graphics,string text,Rectangle bounds,Rectangle clip,Font font,Color background,Color foreground,int inset)
    {
        Rectangle visible=Rectangle.Intersect(bounds,clip);if(visible.Width<=0||visible.Height<=0)return;
        var saved=graphics.Save();
        try
        {
            graphics.SetClip(visible,System.Drawing.Drawing2D.CombineMode.Intersect);
            using(var brush=new SolidBrush(background))graphics.FillRectangle(brush,bounds);
            var label=new Rectangle(bounds.X+inset,bounds.Y,Math.Max(0,bounds.Width-inset-4),bounds.Height);
            if(label.Width>0)TextRenderer.DrawText(graphics,text,font,label,foreground,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix|TextFormatFlags.PreserveGraphicsClipping|TextFormatFlags.PreserveGraphicsTranslateTransform);
            DrawColumnSeparator(graphics,bounds,false);
        }
        finally{graphics.Restore(saved);}
    }
    [DllImport("uxtheme.dll",EntryPoint="#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll",EntryPoint="#133")] static extern bool AllowDarkModeForWindow(IntPtr window,bool allow);
    [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)] static extern int SetWindowTheme(IntPtr window,string app,string id);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("user32.dll")] static extern int GetScrollPos(IntPtr window,int bar);
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
            if(list!=null){
                // Masked native style: buffer this list's paints without changing
                // selection, checkbox/grid styles or composing the entire form.
                const int LvmSetExtendedListViewStyle=0x1036,LvsExDoubleBuffer=0x00010000;
                SendMessage(list.Handle,LvmSetExtendedListViewStyle,new IntPtr(LvsExDoubleBuffer),new IntPtr(LvsExDoubleBuffer));
                IntPtr header=SendMessage(list.Handle,0x101f,IntPtr.Zero,IntPtr.Zero);if(header!=IntPtr.Zero){SetWindowTheme(header,"DarkMode_Explorer",null);var painter=new HeaderTail(list);painter.AssignHandle(header);var frame=new ScrollCorner(list,header);frame.AssignHandle(list.Handle);list.Disposed+=delegate{painter.ReleaseHandle();frame.ReleaseHandle();};}}
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
            if(!owner.IsDisposed&&(message.Msg==0x000f||message.Msg==0x0317||message.Msg==0x0318))
            {
                // Explorer paints two-pixel etched rails below the last item. Replace
                // that blank area with the same one-pixel separators used by owner draw.
                Rect head;
                if(GetClientRect(header,out head))
                {
                    int emptyTop=Math.Max(head.Bottom,owner.Items.Count==0?head.Bottom:owner.Items[owner.Items.Count-1].Bounds.Bottom);
                    if(emptyTop<owner.ClientSize.Height)
                    using(var graphics=message.Msg==0x000f?Graphics.FromHwnd(Handle):Graphics.FromHdc(message.WParam))
                    {
                        using(var brush=new SolidBrush(owner.BackColor))graphics.FillRectangle(brush,0,emptyTop,owner.ClientSize.Width,owner.ClientSize.Height-emptyTop);
                        int x=-GetScrollPos(Handle,0);
                        foreach(ColumnHeader column in owner.Columns)
                        {
                            DrawColumnSeparator(graphics,new Rectangle(x,emptyTop,column.Width,owner.ClientSize.Height-emptyTop),false);
                            x+=column.Width;
                        }
                    }
                }
            }
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
