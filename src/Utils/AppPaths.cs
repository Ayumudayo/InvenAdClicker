using System;
using System.IO;

namespace InvenAdClicker.Utils
{
    public sealed class AppPaths
    {
        public string DeploymentDirectory { get; }
        public string DataDirectory { get; }
        public string SettingsFilePath => Path.Combine(DataDirectory, "appsettings.json");
        public string CredentialFilePath => Path.Combine(DataDirectory, "credentials.dat");
        public string LogDirectory => Path.Combine(DataDirectory, "logs");
        public string SettingsTemplatePath => Path.Combine(DeploymentDirectory, "appsettings.json");

        public AppPaths(string deploymentDirectory, string dataDirectory)
        {
            DeploymentDirectory = Path.GetFullPath(deploymentDirectory);
            DataDirectory = Path.GetFullPath(dataDirectory);
        }

        public static AppPaths CreateDefault() => new AppPaths(AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InvenAdClicker"));

        public static AppPaths CreateIsolated(string deploymentDirectory, string dataDirectory) =>
            new AppPaths(deploymentDirectory, dataDirectory);
    }

    internal static class AtomicFile
    {
        public static void Write(string path, byte[] bytes, bool overwrite)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
