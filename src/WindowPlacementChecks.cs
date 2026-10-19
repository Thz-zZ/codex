using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace RelayLite {
internal static class WindowPlacementChecks {
    static void Check(bool condition,string message){if(!condition)throw J.Error("Window placement: "+message);}
    internal static void Run(string root,List<string> checks) {
        string file=Path.Combine(root,"window-placement","window-state.json");
        Check(WindowPlacement.Read(file)==null,"missing preferences keep default startup behavior");
        foreach(string invalid in new[]{"not json","null","[]","{}","{\"x\":1,\"y\":2,\"width\":0,\"height\":800}","{\"x\":1,\"y\":2,\"width\":1200,\"height\":-1}","{\"x\":1e30,\"y\":2,\"width\":1200,\"height\":800}","{\"x\":1.5,\"y\":2,\"width\":1200,\"height\":800}","{\"x\":1,\"y\":2,\"width\":true,\"height\":800}"}){
            J.Atomic(file,invalid);Check(WindowPlacement.Read(file)==null,"invalid or corrupt preferences do not break startup");
        }
        var bounds=new Rectangle(-1450,80,1100,760);
        J.Atomic(file,J.Encode(J.Map("x",bounds.X,"y",bounds.Y,"width",bounds.Width,"height",bounds.Height,"maximized",true)));
        var saved=WindowPlacement.Read(file);
        Check(saved!=null&&saved.Bounds==bounds&&saved.Maximized,"normal bounds and maximized state survive disk serialization, including negative monitor coordinates");
        var primary=new Rectangle(0,0,1920,1040);var left=new Rectangle(-1600,0,1600,860);var minimum=new Size(960,650);
        Check(WindowPlacement.WorkingArea(bounds,new[]{primary,left},primary)==left,"valid secondary monitor selected by actual overlap");
        Check(WindowPlacement.Fit(bounds,left,minimum)==bounds,"valid saved bounds are not unnecessarily resized or centered");
        var disconnected=new Rectangle(3500,100,1100,760);
        var fallback=WindowPlacement.WorkingArea(disconnected,new[]{primary},primary);
        var fitted=WindowPlacement.Fit(disconnected,fallback,minimum);
        Check(fallback==primary&&primary.Contains(fitted)&&fitted.Size==disconnected.Size,"disconnected monitor falls back to a reachable centered window without losing its size");
        var oversized=WindowPlacement.Fit(new Rectangle(-50,-50,4000,3000),primary,minimum);
        Check(oversized==primary,"large saved windows fit the current monitor work area");
        var tinyScreen=new Rectangle(0,0,800,600);
        Check(WindowPlacement.Fit(bounds,tinyScreen,minimum)==tinyScreen,"small displays do not leave title bar or edges offscreen");
        var undersized=WindowPlacement.Fit(new Rectangle(50,70,200,100),primary,minimum);
        Check(undersized==new Rectangle(50,70,960,650),"minimum usable window size is enforced");
        var shifted=new Rectangle(0,40,1920,1000);
        Check(shifted.Contains(WindowPlacement.Fit(new Rectangle(100,0,1200,800),shifted,minimum)),"taskbar work-area offsets keep the title bar reachable");
        checks.Add("window preference serialization, corrupt-data fallback and maximized flag without minimized geometry");
        checks.Add("window bounds validation, negative-coordinate monitors, disconnected displays, work-area clamping and minimum size");
    }
}
}
