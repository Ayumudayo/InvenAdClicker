using System;
using System.IO;
using System.Runtime.InteropServices;

namespace InvenAdClicker.Utils
{
    public static class PlaywrightDeployment
    {
        public static void VerifyAssets(string directory)
        {
            string platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win32_x64"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "darwin-arm64" : "darwin-x64")
                    : (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64");
            string nodeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";
            foreach (var relativePath in new[]
            {
                "appsettings.json", "Microsoft.Playwright.dll", "playwright.ps1",
                Path.Combine(".playwright", "package", "cli.js"),
                Path.Combine(".playwright", "node", platform, nodeName)
            })
            {
                var path = Path.Combine(directory, relativePath);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Required Playwright deployment file is missing: {relativePath}", path);
            }
        }
    }
}
