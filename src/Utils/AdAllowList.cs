namespace InvenAdClicker.Utils
{
    public static class AdAllowList
    {
        public const string Contains = "https://zicf.inven.co.kr/RealMedia";

        public static bool IsAllowed(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.Contains(Contains, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}

