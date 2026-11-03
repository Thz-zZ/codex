using System.Reflection;

[assembly: AssemblyTitle("Codex 中转台")]
[assembly: AssemblyProduct("Codex 中转台")]
[assembly: AssemblyDescription("Codex 中转管理与用量工作台")]
[assembly: AssemblyVersion(RelayLite.AppVersion.FileVersion)]
[assembly: AssemblyFileVersion(RelayLite.AppVersion.FileVersion)]
[assembly: AssemblyInformationalVersion(RelayLite.AppVersion.Number)]

namespace RelayLite {
internal static class AppVersion {
    public const string Number="1.2.10";
    public const string FileVersion=Number+".0";
}
}
