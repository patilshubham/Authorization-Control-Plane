using System.Security.Cryptography;
using System.Text;

namespace Authorization.Api.Ai;

/// <summary>
/// Produces stable, non-reversible pseudonyms for subject emails so PII never leaves the process in
/// an AI prompt. The same email maps to the same token within a request scope (via a per-instance
/// salt), which lets the model reference "user-ab12cd34" consistently without ever seeing the real
/// address. Shared by the access-certification (F4) and audit-narrative (F9) surfaces.
/// </summary>
public sealed class SubjectPseudonymizer
{
    private readonly byte[] salt = RandomNumberGenerator.GetBytes(16);
    private readonly Dictionary<string, string> tokensByEmail = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a stable pseudonymous token for <paramref name="email"/>. Blank input yields a
    /// generic placeholder; the real address is never included in the token.
    /// </summary>
    public string Pseudonymize(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "unknown-subject";
        }

        if (tokensByEmail.TryGetValue(email, out string? existing))
        {
            return existing;
        }

        byte[] input = Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant());
        byte[] hash = SHA256.HashData([.. salt, .. input]);
        string token = "user-" + Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
        tokensByEmail[email] = token;
        return token;
    }
}
