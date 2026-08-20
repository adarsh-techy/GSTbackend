namespace GSTAutoPilot.Infrastructure.Services.Advisor;

public class AdvisorOptions
{
    public const string SectionName = "Advisor";

    // Master switch. The advisor is only constructed when this is true AND an
    // ApiKey is present — keeps the feature dark on servers without a key.
    public bool Enabled { get; set; }

    // Anthropic API key. Keep this out of appsettings.json in real deployments
    // — set it via user-secrets or the Advisor__ApiKey environment variable.
    public string ApiKey { get; set; } = string.Empty;

    // Claude model id. Default Opus 4.8; swap to claude-sonnet-4-6 /
    // claude-haiku-4-5 to trade reasoning depth for cost.
    public string Model { get; set; } = "claude-opus-4-8";

    public int MaxTokens { get; set; } = 8000;
}
