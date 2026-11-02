using System.Security.Cryptography;
using System.Text;

namespace RentNearBy.Core.Models;

// Seeded districts and cities get the same Guid on every database reset (name-based, SHA-1, version 5 layout).
// District: position of the record in districts.json. City: its Atlas CityKey. Both files are append-only.
public static class StableIds
{
    public static Guid District(int position) => FromName($"bakhli:district:{position}");

    public static Guid City(long cityKey) => FromName($"bakhli:city:{cityKey}");

    private static Guid FromName(string name)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(name));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
