using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using InvenAdClicker.helper;

namespace InvenAdClicker.Helper
{
    /// <summary>
    /// Provides methods to encrypt and decrypt data using the system's security features.
    /// </summary>
    public class Encryption : IDisposable
    {
        private bool _disposedValue;

        private const string CredentialsFilePath = "cred.dat";

        private static byte[] EncryptData(string data)
        {
            try
            {
                byte[] encryptedData = ProtectedData.Protect(
                    Encoding.Unicode.GetBytes(data),
                    null,
                    DataProtectionScope.LocalMachine);
                return encryptedData;
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
                string data = id + ":" + password;
                byte[] encryptedData = EncryptData(data);
                if (encryptedData == null)
                {
                    Logger.Error($"SaveCredentials error: Encrypting Failed");
                }

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
                    id = password = "nofile";
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
                string id, pw = "";

                Console.WriteLine("Enter ID and Password");
                Console.Write("ID : ");
                id = Console.ReadLine();

                Console.Write("PW : ");
                while (true)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        break;
                    }
                    else if (key.Key == ConsoleKey.Backspace)
                    {
                        if (pw.Length > 0)
                        {
                            pw = pw.Substring(0, pw.Length - 1);
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
            bool loadCredRes = TryLoadCredentials(out id, out pw);

            if (!loadCredRes)
            {
                Logger.Error("Error loading or validating credentials.");
                EnterCredentials();
                TryLoadCredentials(out id, out pw);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // Dispose managed resources if any
                }

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
