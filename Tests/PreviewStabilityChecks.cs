using EveCommandCenter.Services;

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
    }
}
