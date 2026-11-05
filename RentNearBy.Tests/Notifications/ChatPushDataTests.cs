namespace RentNearBy.Tests.Notifications;

public class ChatPushDataTests
{
    private static readonly Guid Conversation = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Message = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    [Fact]
    public void WithMessage_CarriesIdAndUnixMs()
    {
        var data = ChatPushData.Build(Conversation, "Asha", "Rent 6000", Message, Dates.Utc(2026, 11, 5, 10, 0, 0).AddMilliseconds(200));

        Assert.Equal(Conversation.ToString(), data[ChatPushData.ConversationId]);
        Assert.Equal("Asha", data[ChatPushData.Title]);
        Assert.Equal("Rent 6000", data[ChatPushData.Body]);
        Assert.Equal(Message.ToString(), data[ChatPushData.MessageId]);
        Assert.Equal("1793872800200", data[ChatPushData.CreatedAt]);
    }

    [Fact]
    public void OrderKey_LaterMessageSortsAfter()
    {
        var first = ChatPushData.Build(Conversation, "A", "Yes available", Message, Dates.Utc(2026, 11, 5, 10));
        var second = ChatPushData.Build(Conversation, "A", "Rent 6000", Guid.NewGuid(), Dates.Utc(2026, 11, 5, 10).AddMilliseconds(200));

        Assert.True(long.Parse(first[ChatPushData.CreatedAt]) < long.Parse(second[ChatPushData.CreatedAt]));
    }

    [Fact]
    public void OldPayload_KeepsTheThreeKeysOnly()
    {
        var data = ChatPushData.Build(Conversation, "A", "B", null, null);

        Assert.Equal(3, data.Count);
        Assert.False(data.ContainsKey(ChatPushData.MessageId));
        Assert.False(data.ContainsKey(ChatPushData.CreatedAt));
    }
}
