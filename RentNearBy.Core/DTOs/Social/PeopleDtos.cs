namespace RentNearBy.Core.DTOs.Social;

public class FollowStateDto
{
    public string State { get; set; } = string.Empty;
}

public class IsBuddyDto
{
    public bool IsBuddy { get; set; }
}

public class PeoplePageDto<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
    public string? NextCursor { get; set; }
    public int Total { get; set; }
}

public class FollowersPageDto : PeoplePageDto<PersonRowDto>
{
    public int Buddies { get; set; }
}
