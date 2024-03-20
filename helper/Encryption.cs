using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace InvenAdClicker.helper
{
    /// <summary>
    /// Provides methods to encrypt and decrypt data using the system's security features.
    /// </summary>
    public class Encryption : IDisposable
    {
        // Logger instance for logging errors
        private bool _disposedValue;

        // File path for storing encrypted credentials
        private const string CredentialsFilePath = "cred.dat";

        /// <summary>
        /// Encrypts a string using the Data Protection API with the LocalMachine scope.
        /// </summary>
        /// <param name="data">The string to be encrypted.</param>
        /// <returns>The encrypted data as a byte array.</returns>
        private static byte[] EncryptData(string data)
        {
            try
            {
                // Encrypts the data
                byte[] encryptedData = ProtectedData.Protect(
                    Encoding.Unicode.GetBytes(data),
                    null,
                    DataProtectionScope.LocalMachine);
                return encryptedData;
            }
            catch (Exception ex)
            {
                // Logs any exception that occurs during the encryption process
                Logger.Error($"EncryptData error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Decrypts a byte array using the Data Protection API with the LocalMachine scope.
        /// </summary>
        /// <param name="encryptedData">The data to be decrypted.</param>
        /// <returns>The decrypted string.</returns>
        private static string DecryptData(byte[] encryptedData)
        {
            try
            {
                // Decrypts the data
                byte[] decryptedData = ProtectedData.Unprotect(
                    encryptedData,
                    null,
                    DataProtectionScope.LocalMachine);
                return Encoding.Unicode.GetString(decryptedData);
            }
            catch (Exception ex)
            {
                // Logs any exception that occurs during the decryption process
                Logger.Error($"DecryptData error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Encrypts and saves credentials to a file.
        /// </summary>
        /// <param name="id">The user ID.</param>
        /// <param name="password">The password.</param>
        /// <returns>True if saving is successful, otherwise false.</returns>
        private void SaveCredentials(string id, string password)
        {
            try
            {
                // Concatenates ID and password with a colon separator
                string data = id + ":" + password;
                byte[] encryptedData = EncryptData(data);
                if (encryptedData == null)
                {
                    Logger.Error($"SaveCredentials error: Encrypting Failed");
                }

                // Writes the encrypted data to a file
                File.WriteAllBytes(CredentialsFilePath, encryptedData);
            }
            catch (Exception ex)
            {
                // Logs any exception that occurs during saving
                Logger.Error($"SaveCredentials error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Attempts to load and decrypt credentials from a file.
        /// </summary>
        /// <param name="id">Output parameter for the user ID.</param>
        /// <param name="password">Output parameter for the password.</param>
        /// <returns>True if loading and decryption are successful, otherwise false.</returns>
        private bool TryLoadCredentials(out string id, out string password)
        {
            id = password = null;
            try
            {
                // Checks if the credentials file exists
                if (!File.Exists(CredentialsFilePath))
                {
                    id = password = "nofile";
                    return false;
                }

                // Reads the encrypted data from the file and decrypts it
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
                // Logs any exception that occurs during loading
                Logger.Error($"TryLoadCredentials error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Interactively prompts the user to enter their credentials, which are then encrypted and saved.
        /// </summary>
        /// <returns>True if the credentials are successfully entered, encrypted, and saved; otherwise false.</returns>
        public bool EnterCredentials()
        {
            try
            {
                string id, pw = null;

                // Prompts user for ID and password
                Console.WriteLine("Enter ID and Password");
                Console.Write("ID : ");
                id = Console.ReadLine();

                Console.Write("PW : ");
                while (true)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true); // Hides the key press from display
                    if (key.Key == ConsoleKey.Enter) // Breaks loop on Enter key
                    {
                        break;
                    }
                    else if (key.Key == ConsoleKey.Backspace) // Handles backspace
                    {
                        if (pw.Length > 0)
                        {
                            pw = pw.Substring(0, pw.Length - 1);
                            Console.Write("\b \b"); // Erases the last character from the console
                        }
                    }
                    else
                    {
                        pw += key.KeyChar; // Appends character to password
                        Console.Write("*"); // Displays '*' instead of the character
                    }
                }

                // Encrypts and saves the credentials
                SaveCredentials(id, pw);
                return true;
            }
            catch (Exception ex)
            {
                // Logs any exception that occurs during credential entry
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
                    // TODO: 관리형 상태(관리형 개체)를 삭제합니다.
                }

                // TODO: 비관리형 리소스(비관리형 개체)를 해제하고 종료자를 재정의합니다.
                // TODO: 큰 필드를 null로 설정합니다.
                _disposedValue = true;
            }
        }

        // // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
        // ~Encryption()
        // {
        //     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
