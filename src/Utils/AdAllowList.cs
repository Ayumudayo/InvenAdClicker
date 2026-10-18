namespace InvenAdClicker.Utils
{
    public static class AdAllowList
    {
        public const string Contains = Constants.ActualAdLinkPrefix;

        public static bool IsAllowed(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.Contains(Contains, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}

