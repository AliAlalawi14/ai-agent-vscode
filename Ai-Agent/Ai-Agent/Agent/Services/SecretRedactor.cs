using System.Text.RegularExpressions;

namespace Ai_Agent.Agent.Services
{
    /// <summary>
    /// Keeps secrets out of what is sent to the LLM (tool results, editor context) and out of the audit log.
    /// Deliberately narrow so normal code is never altered: JSON "…password/apiKey/token…": "value",
    /// connection-string Password=…; segments, UPPER_CASE_KEY=value lines (.env), and well-known key formats.
    /// Key/certificate files are refused outright.
    /// </summary>
    public static class SecretRedactor
    {
        public const string Marker = "***redacted***";

        private static readonly Regex JsonSecret = new(
            @"(""[^""\r\n]*(?:password|passwd|pwd|secret|apikey|api_key|api-key|token|connectionstring|accesskey|access_key|privatekey|private_key)[^""\r\n]*""\s*:\s*"")([^""\r\n]+)("")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Only inside connection strings: preceded by ; or a quote, followed by ; or a quote
        private static readonly Regex ConnectionStringPassword = new(
            @"(?<=[;""'])(\s*(?:password|pwd)\s*=\s*)([^;""'\r\n]+)(?=[;""'])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // .env style: UPPER_CASE names only, so C# like `token = GetToken();` is untouched
        private static readonly Regex EnvSecret = new(
            @"^(\s*(?:export\s+)?[A-Z0-9_]*(?:KEY|SECRET|TOKEN|PASSWORD|PASSWD)[A-Z0-9_]*=)(\S+)$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex KnownKeyFormats = new(
            @"\b(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,})\b",
            RegexOptions.Compiled);

        private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pem", ".pfx", ".p12", ".key", ".jks", ".keystore", ".snk"
        };

        private static readonly HashSet<string> SecretFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa", ".npmrc", ".pypirc", ".git-credentials"
        };

        public static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            var result = JsonSecret.Replace(text, m => $"{m.Groups[1].Value}{Marker}{m.Groups[3].Value}");
            result = ConnectionStringPassword.Replace(result, m => $"{m.Groups[1].Value}{Marker}");
            result = EnvSecret.Replace(result, m => $"{m.Groups[1].Value}{Marker}");
            result = KnownKeyFormats.Replace(result, Marker);
            return result;
        }

        /// <summary>Private keys, certificates and credential files are never read by the agent.</summary>
        public static bool IsSecretFile(string path)
        {
            var name = Path.GetFileName(path);
            return SecretFileNames.Contains(name) || SecretExtensions.Contains(Path.GetExtension(name));
        }

        /// <summary>A write containing the redaction marker would replace a real secret with the placeholder.</summary>
        public static bool ContainsMarker(string? text) =>
            text != null && text.Contains(Marker, StringComparison.Ordinal);
    }
}
