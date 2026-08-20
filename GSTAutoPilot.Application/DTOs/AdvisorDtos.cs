namespace GSTAutoPilot.Application.DTOs;

// Read-only conversational advisor (v1). The frontend holds the conversation
// and resends the full history each turn — the backend is stateless.
public class AdvisorChatRequest
{
    public List<AdvisorMessage> Messages { get; set; } = new();

    // The period the user is currently looking at (YYYYMM), passed as context
    // so phrases like "this month" resolve.
    public string? Period { get; set; }

    // The active company/GST group the user has selected (e.g.
    // "KSCC Coir Corp · 32AABCT2045G1ZU"). Data is already scoped by the
    // X-Company-Id header; this just lets the advisor name the right entity.
    public string? CompanyLabel { get; set; }
}

public class AdvisorMessage
{
    // "user" or "assistant".
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}

// One grounding tool the advisor consulted, surfaced to the UI so the user can
// see the answer is backed by their real data.
public class AdvisorToolCall
{
    public string Tool { get; set; } = string.Empty;
    public string? Period { get; set; }
}

public class AdvisorChatResponse
{
    public string Reply { get; set; } = string.Empty;
    public List<AdvisorToolCall> ToolsUsed { get; set; } = new();
}
