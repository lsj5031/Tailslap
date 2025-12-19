using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal sealed class SafeWaveInHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hwi);

    public SafeWaveInHandle()
        : base(true) { }

    protected override bool ReleaseHandle()
    {
        // Note: In a production environment, you should ensure waveInStop and
        // waveInUnprepareHeader are called before this.
        // SafeHandle ensures the native resource is released even if the managed object is finalized.
        return waveInClose(handle) == 0;
    }
}
