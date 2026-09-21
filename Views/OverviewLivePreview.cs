using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using EveCommandCenter.Interop;
using Forms = System.Windows.Forms;

namespace EveCommandCenter.Views;

/// <summary>
/// A click-through native DWM surface above a transparent WPF card. Ownership keeps
/// it with the overview; WPF receives input and retains its alarm/hover frame.
/// No PrintWindow, process waits or client activation occurs here.
/// </summary>
public sealed class OverviewLivePreview : FrameworkElement
{
    public static readonly DependencyProperty SourceHwndProperty = DependencyProperty.Register(
        nameof(SourceHwnd), typeof(IntPtr), typeof(OverviewLivePreview), new PropertyMetadata(IntPtr.Zero, Changed));
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.Register(
        nameof(Enabled), typeof(bool), typeof(OverviewLivePreview), new PropertyMetadata(false, Changed));
    public IntPtr SourceHwnd { get => (IntPtr)GetValue(SourceHwndProperty); set => SetValue(SourceHwndProperty,value); }
    public bool Enabled { get => (bool)GetValue(EnabledProperty); set => SetValue(EnabledProperty,value); }
    public static readonly DependencyProperty StateTextProperty=DependencyProperty.Register(
        nameof(StateText),typeof(string),typeof(OverviewLivePreview),new PropertyMetadata("Waiting for live preview"));
    public string StateText {get=>(string)GetValue(StateTextProperty);private set=>SetValue(StateTextProperty,value);}
    private Surface? _surface;
    private Window? _owner;
    private readonly DispatcherTimer _timer;
    private DateTime _retryAfter;

    public OverviewLivePreview()
    {
        IsHitTestVisible=false;
        _timer=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(250)};
        _timer.Tick+=(_,_)=>UpdateSurface();
        Loaded+=(_,_)=> { _owner=Window.GetWindow(this); if(_owner!=null) {
            _owner.LocationChanged+=OwnerChanged; _owner.SizeChanged+=OwnerChanged;
            _owner.IsVisibleChanged+=OwnerVisibilityChanged; _owner.StateChanged+=OwnerChanged;
            _owner.Closed+=OwnerClosed;
        } UpdateSurface(); };
        Unloaded+=(_,_)=>Release();
        IsVisibleChanged+=(_,_)=>UpdateSurface();
        LayoutUpdated+=(_,_)=>UpdateSurface();
    }
    private static void Changed(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        var view=(OverviewLivePreview)d;
        if(e.Property==SourceHwndProperty) { view.DisposeSurface(); view._retryAfter=DateTime.MinValue; }
        view.UpdateSurface();
    }
    private void OwnerChanged(object? sender,EventArgs e)=>UpdateSurface();
    private void OwnerVisibilityChanged(object sender,DependencyPropertyChangedEventArgs e)=>UpdateSurface();
    private void OwnerClosed(object? sender,EventArgs e)=>Release();
    private void Release()
    {
        _timer.Stop(); DisposeSurface();
        if(_owner!=null) {
            _owner.LocationChanged-=OwnerChanged; _owner.SizeChanged-=OwnerChanged;
            _owner.IsVisibleChanged-=OwnerVisibilityChanged; _owner.StateChanged-=OwnerChanged;
            _owner.Closed-=OwnerClosed; _owner=null;
        }
    }
    private void DisposeSurface() { _surface?.Dispose(); _surface=null; }
    private void UpdateSurface()
    {
        if(!Enabled || !IsLoaded || !IsVisible || _owner==null || !_owner.IsVisible ||
            _owner.WindowState==WindowState.Minimized || SourceHwnd==IntPtr.Zero || !User32.IsWindow(SourceHwnd)) {
            _timer.Stop(); DisposeSurface(); StateText="Waiting for live preview"; return;
        }
        if(!_timer.IsEnabled)_timer.Start();
        if(User32.IsIconic(SourceHwnd) || ActualWidth<1 || ActualHeight<1) { _surface?.Hide(); StateText="Minimized - last snapshot"; return; }
        var ownerHandle=new WindowInteropHelper(_owner).Handle;
        if(ownerHandle==IntPtr.Zero || PresentationSource.FromVisual(this)==null)return;
        // Do not draw native pixels outside a scrolled/clipped card. Its snapshot
        // remains visible during partial clipping and live resumes when fully in view.
        for(DependencyObject? ancestor=System.Windows.Media.VisualTreeHelper.GetParent(this); ancestor!=null;
            ancestor=System.Windows.Media.VisualTreeHelper.GetParent(ancestor))
            if(ancestor is ScrollViewer scroll) {
                var rect=TransformToAncestor(scroll).TransformBounds(new Rect(RenderSize));
                if(rect.Left<0 || rect.Top<0 || rect.Right>scroll.ViewportWidth+1 || rect.Bottom>scroll.ViewportHeight+1) {
                    _surface?.Hide();StateText="Snapshot - scroll for live";return;
                }
            }
        if(_surface==null) {
            if(DateTime.UtcNow<_retryAfter)return;
            _surface=new Surface(ownerHandle,SourceHwnd);
            if(!_surface.Ready) { StateText="Snapshot - live unavailable"; DisposeSurface();_retryAfter=DateTime.UtcNow.AddSeconds(5);return; }
        }
        StateText="LIVE - click to switch";
        var topLeft=PointToScreen(new System.Windows.Point());
        var bottomRight=PointToScreen(new System.Windows.Point(ActualWidth,ActualHeight));
        _surface.Place(new System.Drawing.Rectangle((int)Math.Round(topLeft.X),(int)Math.Round(topLeft.Y),
            Math.Max(1,(int)Math.Round(bottomRight.X-topLeft.X)),Math.Max(1,(int)Math.Round(bottomRight.Y-topLeft.Y))),_owner.Topmost,_owner.Opacity);
    }

    private sealed class Surface : Forms.Form
    {
        private IntPtr _thumbnail;
        private readonly IntPtr _ownerHandle;
        private System.Drawing.Rectangle _lastBounds;
        private byte _alpha;
        private bool _isTopmost;
        public bool Ready=>_thumbnail!=IntPtr.Zero;
        protected override bool ShowWithoutActivation=>true;
        protected override Forms.CreateParams CreateParams { get { var cp=base.CreateParams;
            cp.ExStyle|=User32.WS_EX_TOOLWINDOW|User32.WS_EX_NOACTIVATE|User32.WS_EX_LAYERED|User32.WS_EX_TRANSPARENT;
            return cp; } }
        public Surface(IntPtr owner,IntPtr source)
        {
            _ownerHandle=owner; FormBorderStyle=Forms.FormBorderStyle.None;ShowInTaskbar=false;
            AutoScaleMode=Forms.AutoScaleMode.None; BackColor=System.Drawing.Color.FromArgb(7,16,19);
            _thumbnail=DwmApi.RegisterThumbnail(Handle,source);
            User32.SetLayeredWindowAttributes(Handle,0,255,User32.LWA_ALPHA);
        }
        public void Place(System.Drawing.Rectangle bounds,bool topmost,double opacity)
        {
            if(_lastBounds!=bounds) {
                User32.SetWindowPos(
                    Handle,
                    IntPtr.Zero,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    User32.SWP_NOACTIVATE|User32.SWP_NOZORDER);
                _lastBounds=bounds;
                var size=DwmApi.QuerySourceSize(_thumbnail);
                double scale=Math.Min((double)bounds.Width/Math.Max(1,size.Width),(double)bounds.Height/Math.Max(1,size.Height));
                int w=Math.Max(1,(int)(size.Width*scale)),h=Math.Max(1,(int)(size.Height*scale));
                int x=(bounds.Width-w)/2,y=(bounds.Height-h)/2;
                var props=new DwmApi.DWM_THUMBNAIL_PROPERTIES {
                    dwFlags=DwmApi.DWM_TNP.RECTDESTINATION|DwmApi.DWM_TNP.VISIBLE|DwmApi.DWM_TNP.SOURCECLIENTAREAONLY,
                    rcDestination=new DwmApi.RECT(x,y,x+w,y+h),fVisible=true,fSourceClientAreaOnly=true
                };
                DwmApi.DwmUpdateThumbnailProperties(_thumbnail,ref props);
            }

            if(_isTopmost!=topmost) {
                User32.SetWindowPos(
                    Handle,
                    topmost?User32.HWND_TOPMOST:User32.HWND_NOTOPMOST,
                    0,0,0,0,
                    User32.SWP_NOMOVE|User32.SWP_NOSIZE|User32.SWP_NOACTIVATE);
                _isTopmost=topmost;
            }

            byte alpha=(byte)Math.Clamp(opacity*255,0,255);
            if(alpha!=_alpha) {
                User32.SetLayeredWindowAttributes(Handle,0,alpha,User32.LWA_ALPHA);
                _alpha=alpha;
            }

            // Show only after bounds, DWM destination and z-band are ready. This avoids
            // a transient default-sized native surface appearing before placement.
            if(!Visible)Show(new OwnerHandle(_ownerHandle));
        }
        protected override void Dispose(bool disposing)
        {
            DwmApi.UnregisterThumbnail(_thumbnail);_thumbnail=IntPtr.Zero;base.Dispose(disposing);
        }
        private sealed class OwnerHandle(IntPtr handle) : Forms.IWin32Window { public IntPtr Handle=>handle; }
    }
}
