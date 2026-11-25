using System;

namespace InvenAdClicker.Utils
{
    public sealed class SettingsValidationException : InvalidOperationException
    {
        public SettingsValidationException(string message) : base(message) { }
    }
}
