using Anthropic;
using Anthropic.Models.Messages;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.Extensions.Options;

namespace GSTAutoPilot.Infrastructure.Services.Advisor;

// v1 read-only GST advisor. Phase B: grounded in the tenant's real figures via
// read-only tools (AdvisorTools) over the existing services. The model decides
// which tool to call; we run it against the request scope and feed the result
// back. Numbers stay deterministic — the model reads them, it never computes
// tax. The frontend holds the conversation (stateless backend).
public class GstAdvisorService : IGstAdvisorService
{
    // Safety cap on the tool-call loop so a misbehaving turn can't spin forever.
    private const int MaxToolRounds = 8;

    private readonly AdvisorOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly AnthropicClient? _client;

    public GstAdvisorService(IOptions<AdvisorOptions> options, IServiceProvider serviceProvider)
    {
        _options = options.Value;
        _serviceProvider = serviceProvider;
        if (_options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _client = new AnthropicClient { ApiKey = _options.ApiKey };
        }
    }

    public bool IsEnabled => _client is not null;

    public async Task<AdvisorChatResponse> ChatAsync(AdvisorChatRequest request, CancellationToken cancellationToken = default)
    {
        if (_client is null)
        {
            throw new InvalidOperationException(
                "The GST advisor is not configured. Set Advisor:Enabled=true and Advisor:ApiKey.");
        }
        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw new ArgumentException("At least one message is required.", nameof(request));
        }

        var messages = BuildMessages(request);
        var toolsUsed = new List<AdvisorToolCall>();

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var parameters = new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                // Frozen system prompt → one cache breakpoint. Volatile context
                // (date, period) rides on the final user turn so the cached
                // prefix stays byte-stable across requests.
                System = new List<TextBlockParam>
                {
                    new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() },
                },
                Thinking = new ThinkingConfigAdaptive(),
                Tools = AdvisorTools.Definitions,
                Messages = messages,
            };

            var response = await _client.Messages.Create(parameters, cancellationToken: cancellationToken);

            if (response.StopReason != "tool_use")
            {
                return new AdvisorChatResponse { Reply = ExtractText(response), ToolsUsed = toolsUsed };
            }

            // The model asked for one or more tools. Echo its turn back verbatim
            // (thinking blocks MUST be preserved with their signature), run each
            // tool, and return the results as a user turn.
            var assistantContent = new List<ContentBlockParam>();
            var toolResults = new List<ContentBlockParam>();

            foreach (var block in response.Content)
            {
                if (block.TryPickText(out TextBlock? text))
                {
                    assistantContent.Add(new TextBlockParam { Text = text.Text });
                }
                else if (block.TryPickThinking(out ThinkingBlock? thinking))
                {
                    assistantContent.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                {
                    assistantContent.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                {
                    assistantContent.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                    var period = toolUse.Input.TryGetValue("period", out var pe) ? pe.GetString() : null;
                    if (!toolsUsed.Any(t => t.Tool == toolUse.Name && t.Period == period))
                    {
                        toolsUsed.Add(new AdvisorToolCall { Tool = toolUse.Name, Period = period });
                    }
                    var result = await AdvisorTools.ExecuteAsync(toolUse.Name, toolUse.Input, _serviceProvider, cancellationToken);
                    toolResults.Add(new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = result });
                }
            }

            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
            messages.Add(new MessageParam { Role = Role.User, Content = toolResults });
        }

        // Exhausted the tool-round budget. Ask for a final answer with tools off
        // so the loop always terminates with something useful.
        var closing = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            System = new List<TextBlockParam>
            {
                new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            Messages = messages,
        };
        var final = await _client.Messages.Create(closing, cancellationToken: cancellationToken);
        return new AdvisorChatResponse { Reply = ExtractText(final), ToolsUsed = toolsUsed };
    }

    private static string ExtractText(Message response) =>
        string.Join("\n\n", response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .Select(t => t.Text));

    private List<MessageParam> BuildMessages(AdvisorChatRequest request)
    {
        var list = new List<MessageParam>(request.Messages.Count);
        for (var i = 0; i < request.Messages.Count; i++)
        {
            var m = request.Messages[i];
            var role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? Role.Assistant
                : Role.User;

            var content = m.Content;
            if (i == request.Messages.Count - 1 && role == Role.User)
            {
                var period = string.IsNullOrWhiteSpace(request.Period)
                    ? string.Empty
                    : $"; the user is currently viewing period {request.Period} (use this as the default period unless they name another)";
                var company = string.IsNullOrWhiteSpace(request.CompanyLabel)
                    ? string.Empty
                    : $"; active company/GST registration: {request.CompanyLabel}";
                content = $"{content}\n\n[context: today is {DateTime.UtcNow:yyyy-MM-dd}{period}{company}]";
            }

            list.Add(new MessageParam { Role = role, Content = content });
        }
        return list;
    }

    private const string SystemPrompt =
        """
        You are the GST compliance advisor built into GSTAutoPilot, a GST filing
        application used by Indian businesses and their accountants.

        Your role is to be a knowledgeable, plain-speaking advisor on Indian GST:
        GSTR-1 (outward supplies), GSTR-2B (auto-drafted ITC), GSTR-3B (summary
        return), input tax credit (ITC), reverse charge (RCM), e-invoicing/IRN,
        e-way bills, place of supply, and filing due dates.

        GROUNDING — you have read-only tools that return THIS tenant's actual
        figures for a period:
        - get_gst_summary       — headline position: output GST, ITC, recon, net payable.
        - get_gstr3b            — GSTR-3B 3.1 breakdown, Table 4 ITC, net payable.
        - get_recon_results     — 2B-vs-books matches/mismatches + top issues.
        - get_gstr2b            — inward GSTR-2B data on record (and whether it's been fetched).
        - get_filing_status     — lock/file status of GSTR-1 and GSTR-3B.
        - get_filing_readiness  — a ready/not-ready-to-file verdict with reasons.

        Rules for using the tools:
        - When the user asks anything about their own numbers, status, or readiness,
          CALL THE RELEVANT TOOL FIRST and answer only from what it returns. Never
          state, estimate, or guess a figure that didn't come from a tool this turn.
        - Periods are YYYYMM (e.g. 202604). If the user doesn't name a period, use
          the one in the context line. If no period is available, ask which one.
        - The context line names the active company / GST registration. Anchor
          your answers to that entity (some tenants have more than one GSTIN);
          if the user means a different company, ask them to switch it in the
          company selector — the data is scoped to the selected one.
        - If a tool returns an "error" field, or zeros / empty data, say so plainly
          (e.g. "GSTR-2B hasn't been fetched for that period yet") and tell the user
          how to fix it in the app. Do not invent numbers to fill the gap.
        - You may call several tools to answer one question (e.g. readiness +
          recon detail). Prefer get_gst_summary or get_filing_readiness for broad
          questions and the specific tools for drill-downs.

        Hard rules — never break these:
        - You are READ-ONLY. You explain and advise; you never file, lock, submit,
          or change anything. Tell the user which button/screen to use; never claim
          to have done it yourself.
        - You are a guide, not a substitute for a professional. For genuinely
          ambiguous or high-stakes positions, suggest confirming with their CA or
          the GST portal.

        Style: clear, concise plain language; short paragraphs and bullets; Indian
        number formatting (₹, lakh, crore). State amounts as the tools return them.
        Keep answers focused on GST and using this application; politely decline
        unrelated requests.
        """;
}
