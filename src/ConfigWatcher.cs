using System;
using System.IO;
using System.Windows.Forms;

namespace RelayLite {
internal sealed class ConfigWatcher:IDisposable {
    readonly Control owner;readonly Action changed;readonly Timer debounce=new Timer {Interval=250};
    FileSystemWatcher watcher;bool disposed;
    internal ConfigWatcher(Control owner,string directory,Action changed){
        this.owner=owner;this.changed=changed;debounce.Tick+=(s,e)=>{debounce.Stop();if(!disposed)changed();};
        try{if(Directory.Exists(directory)){
            watcher=new FileSystemWatcher(directory,"config.toml"){NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size};
            watcher.Changed+=OnChange;watcher.Created+=OnChange;watcher.Deleted+=OnChange;watcher.Renamed+=OnChange;watcher.EnableRaisingEvents=true;
        }}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    void OnChange(object sender,FileSystemEventArgs args){
        if(disposed||owner.IsDisposed||!owner.IsHandleCreated)return;
        try{owner.BeginInvoke((Action)(()=>{if(disposed)return;debounce.Stop();debounce.Start();}));}catch(InvalidOperationException){}
    }
    public void Dispose(){disposed=true;watcher?.Dispose();debounce.Dispose();}
}
internal sealed partial class MainForm {
    ConfigWatcher configWatcher;
    void WatchConfig(){configWatcher?.Dispose();configWatcher=new ConfigWatcher(this,store.ConfigDir,()=>RefreshConfigState());}
    async void RefreshConfigState(){
        if(!Visible||view?.CoreWebView2==null||quitting)return;
        try{await view.CoreWebView2.ExecuteScriptAsync("window.refreshVisibleState?.()");}catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException){}
    }
}
}
