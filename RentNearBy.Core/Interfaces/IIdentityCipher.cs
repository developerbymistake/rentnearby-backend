namespace RentNearBy.Core.Interfaces;

// Encrypts identity numbers (taxi driving licence) at rest. Unprotect throws CryptographicException on a
// wrong key or a tampered value; callers treat that as "unreadable", never log the value.
public interface IIdentityCipher
{
    bool IsConfigured { get; }
    string Protect(string plain);
    string Unprotect(string stored);

    // Purpose = AES-GCM associated data: a blob made for one purpose (e.g. "stay-quote-bank:<userId>") never opens under another,
    // so a value cannot be moved to another owner or into a licence field. The overloads above use the purpose "taxi-dl".
    string Protect(string plain, string purpose);
    string Unprotect(string stored, string purpose);
}
