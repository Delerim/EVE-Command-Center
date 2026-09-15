using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EveCommandCenter.Interop;
using EveCommandCenter.Views;
using Forms=System.Windows.Forms;

internal static partial class Program
{
    // Opt-in desktop smoke test; uses only temporary test windows, never an EVE client.
    private static void CheckNativeOverviewPreview(string? screenshot)
    {
        var app=new System.Windows.Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        using var source=new Forms.Form {Text="Preview test source",Width=320,Height=200,Left=420,Top=80,
            StartPosition=Forms.FormStartPosition.Manual,BackColor=System.Drawing.Color.CornflowerBlue};
        var view=new OverviewLivePreview {Enabled=true,Margin=new Thickness(10)};
        var owner=new Window {Title="Preview test destination",Width=260,Height=180,Left=80,Top=80,
            WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=System.Windows.Media.Brushes.DarkSlateGray,
            Content=view,Topmost=true};
        void Pump() {
            var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(350)};
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
        }
        Forms.Form? Surface()=>(Forms.Form?)typeof(OverviewLivePreview).GetField("_surface",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(view);
        try {
            source.Show();view.SourceHwnd=source.Handle;owner.Show();Pump();
            var surface=Surface();
            Check(surface!=null&&surface.Visible&&view.StateText.StartsWith("LIVE"),"Native DWM surface registers and displays over a transparent WPF owner");
            int flags=User32.GetWindowLong(surface!.Handle,User32.GWL_EXSTYLE);
            Check((flags&User32.WS_EX_TRANSPARENT)!=0&&(flags&User32.WS_EX_NOACTIVATE)!=0,"Live surface passes input through and cannot activate itself");
            var before=surface.Bounds;owner.Left+=40;Pump();
            Check(surface.Left>before.Left,"Native surface follows its owner when moved");
            if(screenshot!=null) {
                var origin=owner.PointToScreen(new System.Windows.Point());
                using var bitmap=new System.Drawing.Bitmap((int)owner.ActualWidth,(int)owner.ActualHeight);
                using(var g=System.Drawing.Graphics.FromImage(bitmap))g.CopyFromScreen((int)origin.X,(int)origin.Y,0,0,bitmap.Size);
                bitmap.Save(screenshot,System.Drawing.Imaging.ImageFormat.Png);
                var pixel=bitmap.GetPixel(bitmap.Width/2,bitmap.Height/2);
                Check(pixel.B>150&&pixel.B>pixel.R,"DWM source pixels appear inside the combined surface");
            }
            view.Enabled=false;Pump();Check(surface.IsDisposed&&Surface()==null,"Disabling live mode disposes its native window and registration");
            view.Enabled=true;Pump();Check(Surface()?.Visible==true,"Live mode can be enabled again without reopening the overview");
            source.WindowState=Forms.FormWindowState.Minimized;Pump();Check(Surface()?.Visible==false&&view.StateText.StartsWith("Minimized"),"Minimized clients expose the snapshot fallback");
            source.WindowState=Forms.FormWindowState.Normal;Pump();Check(Surface()?.Visible==true,"Live preview resumes when the source is restored");
            var restored=Surface();owner.Hide();Pump();Check(restored!.IsDisposed&&Surface()==null,"Hiding the overview releases its native surface");
            owner.Show();Pump();restored=Surface();Check(restored?.Visible==true,"Showing the overview recreates its live surface");owner.Close();Pump();Check(restored!.IsDisposed&&Surface()==null,"Closing the overview releases the native surface");
        } finally {owner.Close();source.Close();}
        Console.WriteLine($"{_checks} native preview checks passed.");
    }
}
