namespace Scale_Eye_Monitor
{
    /*
     * IpValidator
     * -----------
     * Purpose:
     *   Small, strict IPv4 string validator used for user-entered IPv4 endpoints
     *   (currently the weight stream IP).
     *
     * Behavior:
     *   - Requires exactly four dot-separated octets.
     *   - Each octet must be numeric 0–255.
     *   - Rejects leading zeros (e.g. "01", "001") to avoid ambiguous representations.
     *
     * Notes:
     *   - This is intentionally stricter than IPAddress.TryParse() for UI/config hygiene.
     */

    internal static class IpValidator
    {
        public static bool IsStrictIPv4(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;

            var parts = s.Split('.');
            if (parts.Length != 4) return false;

            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 3) return false;
                if (!int.TryParse(part, out var n)) return false;
                if (n < 0 || n > 255) return false;

                // Disallow ambiguous forms like "01" or "001"
                if (part.Length > 1 && part[0] == '0') return false;
            }

            return true;
        }
    }
}
