using System;
using System.IO;
using System.IO.Pipes;
using Windows.Win32;

static class CollectorPipe
{
    public const string Prefix = "traydog-resources-";

    public static void VerifyPeer(PipeStream pipe, int expectedPid, bool server)
    {
        uint pid;
        bool success = server
            ? PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)
            : PInvoke.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid);
        if (!success || pid != expectedPid)
            throw new IOException("Resource collector pipe identity check failed.");
    }
}
