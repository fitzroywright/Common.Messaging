namespace Common.Messaging.Channels.MicrosoftGraph;

public sealed class MicrosoftGraphTeamsOptions
{
    public string SenderUpn { get; init; } = string.Empty;
    public string DelegatedAccessToken { get; init; } = string.Empty;
    public string DelegatedAccessTokenSecretName { get; init; } = "messaging/msgraph/teams/delegated-access-token";
}
