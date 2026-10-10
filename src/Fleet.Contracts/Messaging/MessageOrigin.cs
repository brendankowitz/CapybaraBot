namespace Fleet.Contracts.Messaging;

/// <summary>Who produced a message. Only <see cref="User"/> can ever constitute consent (FR-A2A-08).</summary>
public enum MessageOrigin
{
    User,
    Agent,
    System,
    Event,
}
