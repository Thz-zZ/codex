using System.Collections.Generic;

namespace RelayLite {
internal sealed partial class MainForm {
    // Keep selections in the host process while WebView2 is released to the tray.
    readonly Dictionary<string,object> detailViewState=J.Map();
    object HandleDetailViewState(object input){
        if(input!=null){
            var values=J.Obj(input);detailViewState.Clear();
            object value;
            if(values.TryGetValue("local",out value))detailViewState["local"]=value;
        }
        return J.Clone(detailViewState);
    }
}
}
