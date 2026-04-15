namespace TelegramToMatrixForward.Matrix;

internal sealed record FormattedMessage : Message
{
    private string FormattedMessageText { get; }

    public FormattedMessage(string formattedMessageText, string messageText) : base(messageText)
    {
        FormattedMessageText = formattedMessageText;
    }

    public override Dictionary<string, string> ToSerializableMessage()
    {
        return new Dictionary<string, string>
        {
            { "msgtype", "m.text" },
            { "body", MessageText },
            { "format", "org.matrix.custom.html" },
            { "formatted_body", FormattedMessageText },
        };
    }
}
