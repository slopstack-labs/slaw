using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SlopstackLabs.Slaw.Mcp;

/// <summary>
/// Keeps stdout for JSON-RPC alone. VST3 plugins are native code with opinions,
/// and some (OB-Xf, for one) print their startup log straight to file descriptor 1 —
/// which, under <c>slaw mcp</c>, is the protocol. On Unix the protocol keeps a
/// private duplicate of stdout and descriptor 1 is pointed at stderr, so anything
/// else that writes there lands in the log instead of in the client's parser.
/// </summary>
internal static class ProtocolStandardOutput
{
    private const int StandardOutputDescriptor = 1;
    private const int StandardErrorDescriptor = 2;

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int DuplicateDescriptor(int descriptor);

    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int DuplicateDescriptorOnto(int descriptor, int targetDescriptor);

    /// <summary>Takes stdout for the protocol. Call once, before any plugin is loaded.</summary>
    public static Stream Claim()
    {
        if (OperatingSystem.IsWindows())
        {
            return Console.OpenStandardOutput();
        }
        try
        {
            Console.Out.Flush();
            var protocolDescriptor = DuplicateDescriptor(StandardOutputDescriptor);
            if (protocolDescriptor < 0)
            {
                return Console.OpenStandardOutput();
            }
            // If this fails, plugins can still scribble on the protocol, but the protocol itself works.
            DuplicateDescriptorOnto(StandardErrorDescriptor, StandardOutputDescriptor);
            return new FileStream(new SafeFileHandle(protocolDescriptor, ownsHandle: true), FileAccess.Write, bufferSize: 0);
        }
        catch (Exception descriptorRemediationOpportunity) when (descriptorRemediationOpportunity is DllNotFoundException or EntryPointNotFoundException)
        {
            return Console.OpenStandardOutput();
        }
    }
}
