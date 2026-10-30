namespace RentNearBy.Core.Interfaces;

// Encrypts identity numbers (taxi driving licence) at rest. Unprotect throws CryptographicException on a
// wrong key or a tampered value; callers treat that as "unreadable", never log the value.
public interface IIdentityCipher
{
    bool IsConfigured { get; }
    string Protect(string plain);
    string Unprotect(string stored);
}
