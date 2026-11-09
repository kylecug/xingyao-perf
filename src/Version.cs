using System.Reflection;

// Bump on every release, together with src/app.manifest, and tag the commit vX.Y.Z.
[assembly: AssemblyTitle("xingyao-perf")]
[assembly: AssemblyProduct("xingyao-perf")]
[assembly: AssemblyVersion("0.9.8.0")]
[assembly: AssemblyFileVersion("0.9.8.0")]
[assembly: AssemblyInformationalVersion("0.9.8")]

namespace XingyaoPerf
{
    internal static class AppVersion
    {
        public const string Text = "0.9.8";
    }
}
