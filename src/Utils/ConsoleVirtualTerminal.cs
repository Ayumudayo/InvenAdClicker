using System;
using System.Runtime.InteropServices;

namespace InvenAdClicker.Utils
{
    internal static class ConsoleVirtualTerminal
    {
        private const int StdOutputHandle = -11;
        private const uint EnableProcessedOutput = 0x0001;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        internal static bool TryEnable(out uint originalMode)
        {
            originalMode = 0;

            if (!OperatingSystem.IsWindows())
            {
                return true;
            }

            IntPtr handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return false;
            }

            if (!GetConsoleMode(handle, out var mode))
            {
                return false;
            }

            originalMode = mode;
            uint newMode = mode | EnableProcessedOutput | EnableVirtualTerminalProcessing;
            if (newMode == mode)
            {
                return true;
            }

            return SetConsoleMode(handle, newMode);
        }

        internal static void TryRestore(uint originalMode)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                IntPtr handle = GetStdHandle(StdOutputHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return;
                }

                SetConsoleMode(handle, originalMode);
            }
            catch
            {
                // Ignore restore failures.
            }
        }
    }

    internal static class Ansi
    {
        public const string EnterAltBuffer = "\x1b[?1049h";
        public const string ExitAltBuffer = "\x1b[?1049l";
        public const string HideCursor = "\x1b[?25l";
        public const string ShowCursor = "\x1b[?25h";
        public const string ClearScreen = "\x1b[2J";
        public const string Home = "\x1b[H";
        public const string ClearToEnd = "\x1b[J";
    }
}
