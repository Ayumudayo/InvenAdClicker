using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using InvenAdClicker.helper;

namespace InvenAdClicker.Helper
{
    public class Encryption : IDisposable
    {
        private bool _disposedValue;
        private const string CredentialsFilePath = "cred.dat";

        private static byte[] EncryptData(string data)
        {
            try
            {
                return ProtectedData.Protect(
                    Encoding.Unicode.GetBytes(data),
                    null,
                    DataProtectionScope.LocalMachine);
            }
            catch (Exception ex)
            {
                Logger.Error($"EncryptData error: {ex.Message}");
                throw;
            }
        }

        private static string DecryptData(byte[] encryptedData)
        {
            try
            {
                byte[] decryptedData = ProtectedData.Unprotect(
                    encryptedData,
                    null,
                    DataProtectionScope.LocalMachine);
                return Encoding.Unicode.GetString(decryptedData);
            }
            catch (Exception ex)
            {
                Logger.Error($"DecryptData error: {ex.Message}");
                throw;
            }
        }

        private void SaveCredentials(string id, string password)
        {
            try
            {
                string data = $"{id}:{password}";
                byte[] encryptedData = EncryptData(data);
                File.WriteAllBytes(CredentialsFilePath, encryptedData);
            }
            catch (Exception ex)
            {
                Logger.Error($"SaveCredentials error: {ex.Message}");
                throw;
            }
        }

        private bool TryLoadCredentials(out string id, out string password)
        {
            id = password = null;
            try
            {
                if (!File.Exists(CredentialsFilePath))
                {
                    return false;
                }

                byte[] encryptedData = File.ReadAllBytes(CredentialsFilePath);
                string decryptedData = DecryptData(encryptedData);
                string[] parts = decryptedData.Split(':');
                if (parts.Length == 2)
                {
                    id = parts[0];
                    password = parts[1];
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"TryLoadCredentials error: {ex.Message}");
                return false;
            }
        }

        public bool EnterCredentials()
        {
            try
            {
                Console.WriteLine("Enter ID and Password");
                Console.Write("ID : ");
                string id = Console.ReadLine();

                Console.Write("PW : ");
                string pw = "";
                while (true)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter)
                        break;
                    else if (key.Key == ConsoleKey.Backspace)
                    {
                        if (pw.Length > 0)
                        {
                            pw = pw[..^1];
                            Console.Write("\b \b");
                        }
                    }
                    else
                    {
                        pw += key.KeyChar;
                        Console.Write("*");
                    }
                }
                Console.WriteLine();
                SaveCredentials(id, pw);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"EnterCredentials error: {ex.Message}");
                return false;
            }
        }

        public void LoadAndValidateCredentials(out string id, out string pw)
        {
            if (!TryLoadCredentials(out id, out pw))
            {
                Logger.Error("Error loading or validating credentials.");
                EnterCredentials();
                if (!TryLoadCredentials(out id, out pw))
                {
                    throw new Exception("Failed to load credentials after entering.");
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                // 관리 리소스 해제 코드 (필요 시)
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
