using System.Collections.Generic;

namespace RelayLite {
internal sealed partial class MainForm {
    // Keep selections in the host process while WebView2 is released to the tray.
    readonly Dictionary<string,object> detailViewState=J.Map();
    object HandleDetailViewState(object input){
        if(input!=null){
            var values=J.Obj(input);detailViewState.Clear();
            foreach(var provider in J.Arr(J.Get(store.List(),"providers"))){
                string id=J.Str(provider,"id");object value;
                if(values.TryGetValue(id,out value))detailViewState[id]=value;
            }
        }
        return J.Clone(detailViewState);
    }
}
}
