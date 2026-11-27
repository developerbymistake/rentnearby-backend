using System.Text.RegularExpressions;
using RentNearBy.Api.Handlers;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Regression;

// CONTRACT_BACKEND_DP 7.3 (handler order), 7.6 (no new join for the photo), 7.9 (privacy), 7.12 (admin removal).
public class ProfilePhotoGuardTests
{
    private static string Root => CompletionCounterGuardTests.Root();
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string Method(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = src.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        var nextInternal = src.IndexOf("\n    internal ", start + signature.Length, StringComparison.Ordinal);
        var end = new[] { next, nextInternal }.Where(i => i > start).DefaultIfEmpty(src.Length).Min();
        return src[start..end];
    }

    [Fact]
    public void Upload_checks_first_then_saves_then_swaps_then_deletes_the_old_file()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "UserPhotoHandlers.cs"), "public static async Task<IResult> UploadPhoto(");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("TryGetUserId") < At("PhotoUpload.Check("));
        Assert.True(At("PhotoUpload.Check(") < At("SaveProfilePhotoAsync("));
        Assert.True(At("SaveProfilePhotoAsync(") < At("SetProfilePhotoSql"));
        Assert.Equal(2, Regex.Matches(body, @"TryDeleteAsync\(photoService, filePath").Count);
        var catchAt = Regex.Match(body, @"catch\r?\n").Index;
        Assert.True(catchAt > At("SetProfilePhotoSql") && body.IndexOf("TryDeleteAsync(photoService, filePath", catchAt, StringComparison.Ordinal) > catchAt);
        Assert.True(At("rows.Count == 0") < body.LastIndexOf("TryDeleteAsync(photoService, filePath", StringComparison.Ordinal));
        Assert.True(At("TryDeleteAsync(photoService, oldPath") > At("rows.Count == 0"));
        Assert.Contains("if (oldPath != filePath)", body);
        Assert.DoesNotContain("DeletePhotoAsync(", body);
        Assert.DoesNotMatch(@"\.Log(Information|Warning|Error|Debug)\(", body);
    }

    [Fact]
    public void Photo_statements_lock_the_row_and_stamp_the_change()
    {
        foreach (var sql in new[] { UserPhotoHandlers.SetProfilePhotoSql, AdminUserPhotoHandlers.ClearProfilePhotoSql })
        {
            Assert.Contains("FOR UPDATE", sql);
            Assert.Contains("\"ProfilePhotoUpdatedAt\" = now()", sql);
            Assert.Contains("RETURNING old.\"ProfilePhotoPath\" AS \"OldPath\"", sql);
        }
        Assert.Contains("WHERE \"Id\" = @user FOR UPDATE", UserPhotoHandlers.SetProfilePhotoSql);
        Assert.Contains("\"ProfilePhotoPath\" IS NOT NULL FOR UPDATE", AdminUserPhotoHandlers.ClearProfilePhotoSql);
        Assert.Contains("SET \"ProfilePhotoUrl\" = NULL, \"ProfilePhotoPath\" = NULL", AdminUserPhotoHandlers.ClearProfilePhotoSql);
    }

    [Fact]
    public void Complete_onboarding_checks_the_reservation_before_the_registered_lookup()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "AuthHandlers.cs"), "public static async Task<IResult> PhoneCompleteOnboarding(");
        var proof = body.IndexOf("ClassifySignupAsync", StringComparison.Ordinal);
        Assert.True(proof > body.IndexOf("validator.ValidateAsync", StringComparison.Ordinal));
        Assert.True(proof < body.IndexOf("IsPhoneVerifiedByAnyUserAsync", StringComparison.Ordinal));
        Assert.Contains("BadRequestResponse(\"Please verify your phone number again.\", \"OtpRequired\")", body);
    }

    // Each reader reads the photo in the statement that already reads the person: the Users join count is today's count.
    // BuddyListService: 6 + 3 seeker-role joins of the buddy counts (seeker-only contract section 3), none reads a photo.
    [Theory]
    [InlineData("RentNearBy.Infrastructure/Services/Buddy/TripAccess.cs", 3, 2)]
    [InlineData("RentNearBy.Infrastructure/Services/Buddy/Be1/BuddyFindService.cs", 1, 1)]
    [InlineData("RentNearBy.Infrastructure/Services/Buddy/Be1/BuddyListService.cs", 10, 6)]   // + invite candidates (I-13)
    [InlineData("RentNearBy.Infrastructure/Services/Buddy/Be1/BuddyRequestService.cs", 4, 2)]
    [InlineData("RentNearBy.Infrastructure/Services/Buddy/Be2/Be2Sql.cs", 3, 1)]
    [InlineData("RentNearBy.Infrastructure/Services/Buddy/Be2/TripMemberService.cs", 3, 2)]
    [InlineData("RentNearBy.Infrastructure/Services/Buddy/Be2/TripSharingHooks.cs", 3, 2)]
    [InlineData("RentNearBy.Infrastructure/Services/Buddy/Be2/TripViewerReader.cs", 2, 2)]
    public void Readers_get_the_photo_without_a_new_users_join(string file, int usersJoins, int photoColumns)
    {
        var src = Src(file.Split('/'));
        Assert.Equal(usersJoins, Regex.Matches(src, "(JOIN|FROM) \"Users\"").Count);
        Assert.Equal(photoColumns, Regex.Matches(src, "\"ProfilePhotoUrl\"").Count);
        Assert.Contains("CloudinaryThumb.PersonFace", src.Contains("Be2Sql") ? Src("RentNearBy.Infrastructure", "Services", "Buddy", "Be2", "TripInviteService.cs") : src);
    }

    [Fact]
    public void Chat_reads_name_and_photo_in_the_same_lookups()
    {
        var src = Src("RentNearBy.Api", "Handlers", "ChatHandlers.cs");
        Assert.Contains("Select(u => new { u.Id, u.Name, u.ProfilePhotoUrl })", src);
        Assert.Contains("Select(u => new { u.Name, u.ProfilePhotoUrl })", src);
        Assert.DoesNotContain("Select(u => u.Name)", Method(src, "internal static async Task<ConversationDto> BuildConversationDtoAsync("));
        Assert.Contains("OtherPartyPhotoUrl = CloudinaryThumb.Apply(otherPartyPhoto, CloudinaryThumb.PersonFace)", src);
    }

    private static readonly string[] AllowedPhotoFiles =
    [
        "RentNearBy.Api/Handlers/AdminHandlers.cs",
        "RentNearBy.Api/Handlers/AdminUserPhotoHandlers.cs",
        "RentNearBy.Api/Handlers/ChatHandlers.cs",
        "RentNearBy.Api/Handlers/UserPhotoHandlers.cs",
        "RentNearBy.Api/Mappings/DtoMappings.cs",
        "RentNearBy.Core/DTOs/Responses/AdminUserDto.cs",
        "RentNearBy.Core/DTOs/Responses/UserDto.cs",
        "RentNearBy.Core/Entities/User.cs",
        "RentNearBy.Infrastructure/Data/ApplicationDbContext.cs",
        "RentNearBy.Infrastructure/Services/Buddy/Be1/BuddyFindService.cs",
        "RentNearBy.Infrastructure/Services/Buddy/Be1/BuddyListService.cs",
        "RentNearBy.Infrastructure/Services/Buddy/Be1/BuddyRequestService.cs",
        "RentNearBy.Infrastructure/Services/Buddy/Be2/Be2Sql.cs",
        "RentNearBy.Infrastructure/Services/Buddy/Be2/TripMemberService.cs",
        "RentNearBy.Infrastructure/Services/Buddy/Be2/TripSharingHooks.cs",
        "RentNearBy.Infrastructure/Services/Buddy/Be2/TripViewerReader.cs",
        "RentNearBy.Infrastructure/Services/Buddy/TripAccess.cs",
        // Bakhli social: faces of people rows, posts, chats and notice actors (always the PersonFace thumb).
        "RentNearBy.Infrastructure/Services/Buddy/Be4/NoticeReader.cs",
        "RentNearBy.Infrastructure/Services/Social/PublicTripReader.cs",
        "RentNearBy.Infrastructure/Services/Social/SocialChatService.cs",
        "RentNearBy.Infrastructure/Services/Social/SocialProfileService.cs",
        "RentNearBy.Infrastructure/Services/Social/SocialSql.cs",
    ];

    [Fact]
    public void The_picture_appears_only_in_the_listed_files()
    {
        var found = new[] { "RentNearBy.Api", "RentNearBy.Core", "RentNearBy.Infrastructure" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(Root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj|Migrations)[\\/]"))
            .Where(f => File.ReadAllText(f).Contains("ProfilePhotoUrl", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Order()
            .ToArray();
        Assert.Equal(AllowedPhotoFiles.Order().ToArray(), found);
    }

    [Theory]
    [InlineData("RentNearBy.Infrastructure/Services/FcmService.cs")]
    [InlineData("RentNearBy.Core/Entities/DeletedAccountRecord.cs")]
    [InlineData("RentNearBy.Infrastructure/Services/AccountDeletionService.cs")]
    [InlineData("RentNearBy.Api/Handlers/AdminTaxiHandlers.cs")]
    public void The_picture_never_enters_pushes_deleted_records_or_taxi_paths(string file)
    {
        var path = Path.Combine([Root, .. file.Split('/')]);
        if (!File.Exists(path)) return;
        var src = File.ReadAllText(path);
        Assert.DoesNotContain("ProfilePhoto", src);
        Assert.DoesNotContain("PersonFace", src);
    }

    [Fact]
    public void Admin_removal_writes_the_notice_in_the_transaction_and_deletes_after_commit()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "AdminUserPhotoHandlers.cs"), "public static async Task<IResult> RemoveProfilePhoto(");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("BeginTransactionAsync") < At("ClearProfilePhotoSql"));
        Assert.True(At("db.NotificationEvents.Add(notification)") < At("CommitTransactionAsync"));
        Assert.True(At("CommitTransactionAsync") < At("TryDeleteAsync(photoService, cleared.OldPath"));
        Assert.True(At("TryDeleteAsync(photoService, cleared.OldPath") < At("SendGoLiveNotificationAsync"));
        Assert.Contains("NotFoundResponse(\"No profile photo\")", body);
        Assert.Contains("AdminActionLog.Write(http, \"user.profile_photo.remove\", id)", body);
        Assert.Contains("group.MapDelete(\"/users/{id:guid}/profile-photo\", AdminUserPhotoHandlers.RemoveProfilePhoto).RequireAuthorization(\"AdminOnly\");",
            Src("RentNearBy.Api", "Endpoints", "AdminEndpoints.cs"));
    }

    [Fact]
    public void Admin_removal_notice_is_the_agreed_text_and_route()
    {
        var id = Guid.NewGuid();
        var n = AdminUserPhotoHandlers.BuildNotice(id);
        Assert.Equal(id, n.TargetUserId);
        Assert.Equal(NotificationTypes.ProfilePhotoRemoved, n.Type);
        Assert.Equal("Your profile photo was removed", n.Title);
        Assert.Equal("Please add a new one.", n.Body);
        Assert.Equal("/profile-photo", n.ActionRoute);
        Assert.Equal(UserNotificationRoutes.ProfilePhoto, n.ActionRoute);
        Assert.Equal("profile_photo_removed", NotificationTypes.ToWireValue(NotificationTypes.ProfilePhotoRemoved));
    }
}
