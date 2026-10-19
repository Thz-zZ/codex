using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RelayLite {
// Window preferences are independent of provider/config writes and never store minimized bounds.
internal sealed class WindowPlacement:IDisposable {
    readonly Form form;
    readonly string file;
    readonly Timer saveTimer=new Timer {Interval=400};
    Rectangle normalBounds;
    bool maximized,ready,restoring,disposed;
    string lastSaved;

    internal WindowPlacement(Form form,string directory,bool restore=true) {
        this.form=form;file=Path.Combine(directory,"window-state.json");normalBounds=form.Bounds;
        var saved=restore?Read(file):null;
        if(saved!=null){
            normalBounds=saved.Bounds;maximized=saved.Maximized;
            var area=WorkingArea(normalBounds,Screen.AllScreens.Select(screen=>screen.WorkingArea).ToArray(),Screen.PrimaryScreen.WorkingArea);
            form.MinimumSize=new Size(Math.Min(form.MinimumSize.Width,area.Width),Math.Min(form.MinimumSize.Height,area.Height));
            normalBounds=Fit(normalBounds,area,form.MinimumSize);
            form.StartPosition=FormStartPosition.Manual;form.Bounds=normalBounds;
            if(maximized)form.WindowState=FormWindowState.Maximized;
        }
        form.Shown+=Shown;form.Resize+=Changed;form.LocationChanged+=Changed;
        form.ResizeEnd+=ResizeEnd;form.FormClosing+=Closing;form.Disposed+=Disposed;
        saveTimer.Tick+=(sender,args)=>{saveTimer.Stop();Save();};
    }

    internal sealed class State {
        internal readonly Rectangle Bounds;
        internal readonly bool Maximized;
        internal State(Rectangle bounds,bool maximized){Bounds=bounds;Maximized=maximized;}
    }
    internal static State Read(string path) {
        try{
            var value=J.Read(path,null);if(value==null)return null;
            double? x=J.Num(value,"x"),y=J.Num(value,"y"),width=J.Num(value,"width"),height=J.Num(value,"height");
            if(!x.HasValue||!y.HasValue||!width.HasValue||!height.HasValue)return null;
            foreach(double number in new[]{x.Value,y.Value,width.Value,height.Value})if(number!=Math.Truncate(number))return null;
            if(Math.Abs(x.Value)>100000||Math.Abs(y.Value)>100000||width<100||height<100||width>50000||height>50000)return null;
            return new State(new Rectangle((int)x.Value,(int)y.Value,(int)width.Value,(int)height.Value),J.True(J.Get(value,"maximized")));
        }catch(Exception){return null;} // A missing/corrupt optional preference must not block startup.
    }
    internal static Rectangle WorkingArea(Rectangle bounds,Rectangle[] areas,Rectangle fallback) {
        long best=0;var selected=fallback;
        foreach(var area in areas){
            var overlap=Rectangle.Intersect(bounds,area);long size=(long)Math.Max(0,overlap.Width)*Math.Max(0,overlap.Height);
            if(size>best){best=size;selected=area;}
        }
        return selected;
    }
    internal static Rectangle Fit(Rectangle bounds,Rectangle area,Size minimum) {
        int width=Math.Min(area.Width,Math.Max(Math.Max(1,minimum.Width),bounds.Width));
        int height=Math.Min(area.Height,Math.Max(Math.Max(1,minimum.Height),bounds.Height));
        // A disconnected monitor falls back to the primary work area, not an unreachable position.
        bool offscreen=!bounds.IntersectsWith(area);
        int x=offscreen?area.Left+(area.Width-width)/2:Math.Max(area.Left,Math.Min(bounds.X,area.Right-width));
        int y=offscreen?area.Top+(area.Height-height)/2:Math.Max(area.Top,Math.Min(bounds.Y,area.Bottom-height));
        return new Rectangle(x,y,width,height);
    }

    void Shown(object sender,EventArgs args){ready=true;Changed(sender,args);}
    void Changed(object sender,EventArgs args) {
        if(disposed||!ready||restoring||!form.Visible||form.WindowState==FormWindowState.Minimized)return;
        if(form.WindowState==FormWindowState.Maximized){
            maximized=true;
            if(Valid(form.RestoreBounds))normalBounds=form.RestoreBounds;
        }else if(Valid(form.Bounds)){normalBounds=form.Bounds;maximized=false;}
        saveTimer.Stop();saveTimer.Start();
    }
    bool Valid(Rectangle bounds){return bounds.Width>=Math.Max(100,form.MinimumSize.Width)&&bounds.Height>=Math.Max(100,form.MinimumSize.Height)&&bounds.X!=-32000&&bounds.Y!=-32000;}
    void ResizeEnd(object sender,EventArgs args){Save();}
    void Closing(object sender,FormClosingEventArgs args){Save();}
    void Disposed(object sender,EventArgs args){Dispose();}

    internal void ShowWindow() {
        bool restore=!form.Visible||form.WindowState==FormWindowState.Minimized,wasMaximized=maximized,wasRestoring=restoring;
        restoring=true;
        try{
            // ShowInTaskbar can recreate the native handle; restore only after Show().
            form.ShowInTaskbar=true;form.Show();
            if(restore)form.WindowState=wasMaximized?FormWindowState.Maximized:FormWindowState.Normal;
        }finally{restoring=wasRestoring;}
        Changed(this,EventArgs.Empty);
    }
    internal bool Save() {
        if(disposed||!ready)return false;
        if(!form.IsDisposed)Changed(this,EventArgs.Empty);
        saveTimer.Stop();
        string content=J.Encode(J.Map("version",1,"x",normalBounds.X,"y",normalBounds.Y,"width",normalBounds.Width,"height",normalBounds.Height,"maximized",maximized));
        if(content==lastSaved)return true;
        try{J.Atomic(file,content);lastSaved=content;return true;}
        catch(IOException){return false;}
        catch(UnauthorizedAccessException){return false;}
    }
    public void Dispose() {
        if(disposed)return;
        Save();disposed=true;saveTimer.Dispose();
        form.Shown-=Shown;form.Resize-=Changed;form.LocationChanged-=Changed;
        form.ResizeEnd-=ResizeEnd;form.FormClosing-=Closing;form.Disposed-=Disposed;
    }
}
}
