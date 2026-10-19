using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RelayLite {
internal static class WindowPlacementTestHost {
    static void Check(bool condition,string message){if(!condition)throw J.Error(message);}
    [STAThread] static int Main(string[] args) {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try{
            string directory=Path.GetFullPath(args[0]),mode=args[1],file=Path.Combine(directory,"window-state.json");
            if(mode=="unit"){var checks=new List<string>();WindowPlacementChecks.Run(directory,checks);Console.WriteLine(J.Encode(J.Map("ok",true,"checks",checks)));return 0;}
            using(var form=new Form {Text="Window placement test",Size=new Size(1240,850),MinimumSize=new Size(960,650),StartPosition=FormStartPosition.CenterScreen})
            using(var placement=new WindowPlacement(form,directory)){
                var previous=WindowPlacement.Read(file);
                if(mode=="silent-maximized"){form.WindowState=FormWindowState.Minimized;form.ShowInTaskbar=false;}
                form.Shown+=async(sender,eventArgs)=>{
                    try{
                        await Task.Delay(80);
                        if(mode=="remember-normal"||mode=="immediate-exit"){
                            form.WindowState=FormWindowState.Normal;
                            form.Bounds=WindowPlacement.Fit(new Rectangle(83,97,1080,740),Screen.PrimaryScreen.WorkingArea,form.MinimumSize);
                            var expected=form.Bounds;
                            if(mode=="remember-normal"){await Task.Delay(650);var automatic=WindowPlacement.Read(file);Check(automatic!=null&&automatic.Bounds==expected&&!automatic.Maximized,"resize/move did not save automatically");}
                            // Closing also flushes a resize that has not reached its debounce deadline.
                            form.Close();var written=WindowPlacement.Read(file);
                            Check(written!=null&&written.Bounds==expected&&!written.Maximized,"close did not flush normal bounds");
                        }else if(mode=="restore-normal"){
                            Check(previous!=null&&!previous.Maximized,"normal fixture missing");
                            Check(form.WindowState==FormWindowState.Normal&&form.Bounds==previous.Bounds&&form.StartPosition==FormStartPosition.Manual,"new process did not restore exact normal bounds");
                        }else if(mode=="remember-maximized"){
                            var normal=form.Bounds;form.WindowState=FormWindowState.Maximized;await Task.Delay(80);placement.Save();
                            var written=WindowPlacement.Read(file);
                            Check(form.WindowState==FormWindowState.Maximized&&written!=null&&written.Maximized&&written.Bounds==normal,"maximizing overwrote the normal restore bounds");
                        }else if(mode=="restore-maximized"){
                            Check(previous!=null&&previous.Maximized,"maximized fixture missing");
                            Check(form.WindowState==FormWindowState.Maximized&&form.RestoreBounds==previous.Bounds,"new process did not restore maximized state and normal restore bounds");
                        }else if(mode=="minimize-maximized"||mode=="minimize-normal"||mode=="silent-maximized"){
                            Check(previous!=null,"minimize fixture missing");
                            if(mode!="silent-maximized")form.WindowState=FormWindowState.Minimized;
                            await Task.Delay(60);placement.Save();form.Hide();placement.Save();
                            var written=WindowPlacement.Read(file);
                            Check(written!=null&&written.Bounds==previous.Bounds&&written.Maximized==previous.Maximized,"minimizing or hiding overwrote remembered window state");
                            placement.ShowWindow();await Task.Delay(80);
                            Check(form.WindowState==(previous.Maximized?FormWindowState.Maximized:FormWindowState.Normal),"tray/silent reopen lost the previous maximized state: "+form.WindowState);
                        }else throw J.Error("Unknown test mode: "+mode);
                        Console.WriteLine(J.Encode(J.Map("ok",true,"mode",mode,"saved",J.Read(file,null))));
                    }catch(Exception error){Environment.ExitCode=1;Console.Error.WriteLine(error);}
                    finally{if(!form.IsDisposed)form.Close();}
                };
                Application.Run(form);
            }
            return Environment.ExitCode;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
}
