using System.Security.Cryptography;

namespace RentNearBy.Tests.TaxiRequests;

// The same 34 samples live in the backend, the user app and the admin app; these hashes are the contract's SHA256SUMS.txt.
public class ContractSamplesGuardTests
{
    internal static readonly IReadOnlyDictionary<string, string> Expected = new Dictionary<string, string>
    {
        ["ack_request.json"] = "81ee933300ed8d9fd3f4ab7d49070079f5efb9418c80f393c480baac013b2971",
        ["admin_taxi_bookings.json"] = "527fec54e803003d85b442e0463298d9f13113a564e0fdd755e9e7344305ee1a",
        ["admin_taxi_health.json"] = "79548593a996cd5530aec9a2173ea04e9f91bfb4874cdfe00a2745816905771f",
        ["admin_taxi_request_detail.json"] = "ab0a4694f0e07004e7c164f8d514abf0ec66674604fb525ea5e4031c504b9895",
        ["admin_taxi_request_list.json"] = "401368acfc2111471ba47726d05d548a6cda6ce794c4ccb6a6debc34bb6a32bc",
        ["driver_bookings.json"] = "bfa2312ac0462588413d36b63dad2c81a44ce90bab093b43a28dfc542f0edb79",
        ["driver_device_state.json"] = "211d30f8fc6a43f944633d3759b7e6e7a7ae3826f20937d0fd412b9b76a61b7f",
        ["driver_offer_detail_ringing.json"] = "aa949e5cb7da8ab179683d8ced28b68e40d949eca27976687f56beac9d84664d",
        ["driver_offer_detail_selected.json"] = "c50ed27c83514e54b458a30520486f0e71abdc25328aae1261c08b181511e565",
        ["driver_offer_list.json"] = "e0f98fbf28edcb8450aa41010d95f4591565072846a02ff6a02eb2f8fb95b64b",
        ["driver_rating.json"] = "919e7948bdd3b534040a38ffd8736573d2cf77f53c4cfd525f55fd2360f3c8a8",
        ["driver_readiness.json"] = "928a93253aafde17288d6731107acceb291ad6dec062dcf117bbadf44b7593ef",
        ["error_all_drivers_busy.json"] = "223135df82be387b944801442eafc36d6066ec49774fa5f7549afd46628734f6",
        ["error_dates_already_booked.json"] = "3910a70d81e95e1a4122fe11f40e8b2da8fdd98ca0f9bb33178b93047dd76471",
        ["error_legacy_taxi_active.json"] = "63de1b344925521b308a39af7dc3f6ad4f2d59a8b4eb61e9adfd1fecf3c7d781",
        ["error_plan_locked.json"] = "658376846364c801a49d1e5d8c37c1244fbcb5c67df70279333a88c09bcc7316",
        ["pickup_places.json"] = "93d43cde649a0b2253799d1542fae874eac5cc624c9aa2e35b45ec256c0630be",
        ["push_ring.json"] = "c019ea10fca1893312eb28246e255712316df2e8c2d9de4b10721b2fb9a6ac9d",
        ["push_ring_end.json"] = "d59fa4cfa635f1d275683d48f2b675fb8e5736c8f1201c90fb715268943f1211",
        ["push_visible.json"] = "e540b46988fd65877d09731d7da7bb8cd94e92d2594f068ce439f2bead856d00",
        ["signalr_taxi_request_changed.json"] = "aff476510fbd6bbe2ad3851a94f6b4b6bd4286620c3816bc55fab79bb638c4ae",
        ["taxi_request_booked.json"] = "0a00f02c5f2f57931cfc8c3620fcbaf7b28c88825ddbe1683084b8a71f638c00",
        ["taxi_request_busy.json"] = "638346e2f989b3b4a43bb2bf45d127bcb7e2fb30c13c3c1687e6ec30420e1aa2",
        ["taxi_request_collecting.json"] = "ceb15c8a88b69e80671a889e449160f2a9543c660b575a9b918b1b34145373f6",
        ["taxi_request_create.json"] = "8a5f0d6f9f3bfa2ad734bc5b4561b44e8ab58e47d27e184b44f63f10fa1d1e6f",
        ["taxi_request_finding.json"] = "5134543ef8126e67c272602c98330766d88626fef571c8c51337f0516d977428",
        ["taxi_request_in_progress.json"] = "d5af1d79ff7cbbd24e5361b179bed7ea011bf9591645c4ca55348626dea34535",
        ["taxi_request_no_reply.json"] = "4287a29eabee73ec2dbe078ee9b2b143ccf0ee1200aa3073c4896f6d55f815a7",
        ["taxi_request_rate.json"] = "ff675044ac02e2960cc814a73a12cb0924460c5a10ccf857497fca1dd76c49b3",
        ["taxi_request_selected.json"] = "a410ebf8a252b8e6307f9f40ce450894355db65dc567caed82201f3eb86a17dc",
        ["taxi_request_waiting.json"] = "bfbebbcd50932c4799ba769340f4bd9b95926231d1c5ebaeed5388c833ad270b",
        ["trip_invite.json"] = "21841cd77f8be7bc8642641a28890f957c3b881122abf556b488e09b1ff074d1",
        ["trip_name_fields.json"] = "d04f989d4e2eedc117f54d863a59e751f3492e947b36a915c6290796880cd695",
        ["trip_taxi_fields.json"] = "b8d0e66d4050569510d6828f606b9c0746fb03d8d2d7120f2e644950cb310337",
    };

    internal static string SamplesDir => Path.Combine(AppContext.BaseDirectory, "TaxiRequests", "Samples");

    private static string HashOf(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    public static IEnumerable<object[]> SampleNames() => Expected.Keys.Select(k => new object[] { k });

    [Fact]
    public void There_are_exactly_34_samples_and_the_folder_has_no_others()
    {
        Assert.Equal(34, Expected.Count);
        var onDisk = Directory.GetFiles(SamplesDir, "*.json").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(Expected.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray(), onDisk);
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Sample_hash_equals_the_contract_hash(string name)
        => Assert.Equal(Expected[name], HashOf(Path.Combine(SamplesDir, name)));

    [Fact]
    public void Sums_file_copy_lists_the_same_hashes()
    {
        var lines = File.ReadAllLines(Path.Combine(SamplesDir, "SHA256SUMS.txt")).Where(l => l.Length > 0)
            .Select(l => l.Split("  ", 2)).ToDictionary(p => p[1].Trim(), p => p[0]);
        Assert.Equal(Expected.OrderBy(k => k.Key).ToArray(), lines.OrderBy(k => k.Key).ToArray());
    }
}
