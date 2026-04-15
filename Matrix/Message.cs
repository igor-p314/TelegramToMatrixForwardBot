namespace TelegramToMatrixForward.Matrix;

internal record Message
{
    public string MessageText { get; }

    public Message(string messageText)
    {
        MessageText = messageText;
    }

    public virtual Dictionary<string, string> ToSerializableMessage()
    {
        return new Dictionary<string, string>
        {
            { "msgtype", "m.text" },
            { "body", MessageText },
        };
    }
}
