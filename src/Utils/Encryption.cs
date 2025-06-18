using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace InvenAdClicker.Utils
{
    public class Encryption : IDisposable
    {
        private const string CredentialFile = "credentials.dat";
        private bool _disposed;

        // 저장된 자격증명을 로드
        // 저장된 파일이 없거나 복호화에 실패하면 Console 입력을 통해 새로 받고 저장
        public void LoadAndValidateCredentials(out string id, out string pw)
        {
            if (!TryLoadCredentials(out id, out pw))
            {
                Console.WriteLine("Credentials 파일을 찾을 수 없거나 로드에 실패했습니다.");
                Console.WriteLine("새로운 자격증명을 입력해주세요.");

                EnterCredentials();                         // Console로부터 입력 받아 저장
                if (!TryLoadCredentials(out id, out pw))    // 재시도
                    throw new ApplicationException("자격증명 로딩에 계속 실패했습니다.");
            }
        }

        // Console에서 ID/PW를 입력받아 암호화하여 저장
        public bool EnterCredentials()
        {
            try
            {
                Console.Write("ID: ");
                string id = Console.ReadLine() ?? string.Empty;

                Console.Write("Password: ");
                string pw = ReadPasswordMasked();

                SaveCredentials(id, pw);
                Console.WriteLine("자격증명 저장 완료.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"자격증명 입력/저장 중 오류: {ex.Message}");
                return false;
            }
        }

        // 읽어 복호화 후 ID/PW를 분리 반환
        // 파일이 없거나 포맷이 잘못됐거나 복호화 실패 시 false 반환
        private bool TryLoadCredentials(out string id, out string pw)
        {
            id = pw = string.Empty;

            if (!File.Exists(CredentialFile))
                return false;

            try
            {
                byte[] encrypted = File.ReadAllBytes(CredentialFile);
                byte[] decrypted = ProtectedData.Unprotect(
                    encrypted, null, DataProtectionScope.LocalMachine);

                string txt = Encoding.UTF8.GetString(decrypted);
                var parts = txt.Split(new[] { ':' }, 2);
                if (parts.Length != 2)
                    return false;

                id = parts[0];
                pw = parts[1];
                return true;
            }
            catch
            {
                // 복호화 실패 or 파일 포맷 오류
                return false;
            }
        }

        /// 평문 문자열을 암호화하여 파일에 저장
        private void SaveCredentials(string id, string pw)
        {
            string txt = $"{id}:{pw}";
            byte[] plain = Encoding.UTF8.GetBytes(txt);
            byte[] encrypted = ProtectedData.Protect(
                plain, null, DataProtectionScope.LocalMachine);

            File.WriteAllBytes(CredentialFile, encrypted);
        }

        // Console에 비밀번호 입력 시 마스킹 처리
        private static string ReadPasswordMasked()
        {
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            Console.WriteLine();
            return sb.ToString();
        }


        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
