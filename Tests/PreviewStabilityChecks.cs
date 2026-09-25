using EveCommandCenter.Services;
using System.Reflection;
using EveCommandCenter.Interop;
internal static partial class Program
{
    private static void CheckPreviewStability()
    {
        using var frame = new FrozenFrame(new System.Drawing.Bitmap(8, 8));
        using var copy = frame.Copy();
        frame.Dispose();
        Check(copy != null && copy.Width == 8 && frame.Copy() == null, "Preview owns a usable image after the capture cache is disposed");
        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(2);
        using var finished = new CountdownEvent(2);
        int calls = 0;
        using var captures = new FrozenFrameService(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.Signal();
            release.Wait(TimeSpan.FromSeconds(5));
            finished.Signal();
            return new System.Drawing.Bitmap(8, 8);
        }, _ => true);
        try
        {
            captures.TryCapture((IntPtr)1); captures.TryCapture((IntPtr)2);
            Check(entered.Wait(TimeSpan.FromSeconds(2)), "Two captures run independently without blocking the caller");
            for (int i = 0; i < 1000; i++) { captures.TryCapture((IntPtr)1); captures.TryCapture((IntPtr)3); }
            Check(calls == 2, "Slow captures cannot accumulate duplicate workers or a waiting queue");
            captures.Forget((IntPtr)1);
            release.Set();
            Check(finished.Wait(TimeSpan.FromSeconds(2)) && SpinWait.SpinUntil(() => captures.GetLastFrame((IntPtr)2) != null, 2000), "A completed capture publishes normally");
            Check(SpinWait.SpinUntil(() => captures.PendingCaptures == 0, 2000) && captures.GetLastFrame((IntPtr)1) == null, "A forgotten window cannot be resurrected by a late capture");
            using var retained = captures.GetLastFrame((IntPtr)2)!.Copy();
            captures.Dispose();
            Check(retained!.Width == 8 && captures.GetLastFrame((IntPtr)2) == null, "Capture service shutdown preserves independently owned display copies");
        }
        finally { release.Set(); }
        using var compact=new FrozenFrameService(_=>new System.Drawing.Bitmap(960,540),_=>true){MaximumFrameWidth=480};
        compact.TryCapture((IntPtr)4);
        Check(SpinWait.SpinUntil(()=>compact.GetLastFrame((IntPtr)4)!=null,2000),"Compact overview capture completes asynchronously");
        using var preview=compact.GetLastFrame((IntPtr)4)!.Copy();
        Check(preview!.Width==480&&preview.Height==270,"Compact preview cache bounds bitmap size while preserving aspect ratio");

        var activate=typeof(User32).GetMethod(nameof(User32.ActivateWindow),BindingFlags.Public|BindingFlags.Static)!;
        var inject=typeof(User32).GetMethod(nameof(User32.InjectVirtualKey),BindingFlags.Public|BindingFlags.Static)!;
        var keybd=typeof(User32).GetMethod(nameof(User32.keybd_event),BindingFlags.Public|BindingFlags.Static)!;
        Check(!PreviewCallsMethod(activate,inject)&&!PreviewCallsMethod(activate,keybd),
            "Native client activation contains no synthetic keyboard path");

        var managerActivate=typeof(ThumbnailManager).GetMethod(nameof(ThumbnailManager.ActivateEveWindow),BindingFlags.Public|BindingFlags.Instance)!;
        var replay=typeof(User32).GetMethod(nameof(User32.FixTargetHeldKeys),BindingFlags.Public|BindingFlags.Static)!;
        Check(!PreviewCallsMethod(managerActivate,replay),
            "Preview switching never replays held keyboard or mouse state into a client");

        var canCreate=typeof(ThumbnailManager).GetMethod(
            "CanCreatePreviewForWindow",
            BindingFlags.NonPublic|BindingFlags.Static);
        Check(
            canCreate!=null &&
            !(bool)canCreate.Invoke(
                null,
                new object[]
                {
                    new EveWindow(
                        IntPtr.Zero,
                        "EVE",
                        "Dead")
                })!,
            "Preview batch rejects dead HWNDs before native surface creation");

        var activeBorderGate=typeof(ThumbnailManager).GetField(
            "_activeBorderUiPending",
            BindingFlags.NonPublic|BindingFlags.Instance);
        Check(
            activeBorderGate?.FieldType==typeof(int),
            "Focus event bridge keeps a coalescing gate for border sweeps");
    }

    private static bool PreviewCallsMethod(MethodInfo caller,MethodInfo target)
    {
        byte[] il=caller.GetMethodBody()?.GetILAsByteArray()??Array.Empty<byte>();
        int token=target.MetadataToken;
        for(int i=0;i+4<il.Length;i++)
            if((il[i]==0x28||il[i]==0x6F)&&BitConverter.ToInt32(il,i+1)==token)
                return true;
        return false;
    }
}
