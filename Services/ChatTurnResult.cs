using UniversalAI.Core;

namespace UniversalAI.Services;

public sealed record ChatTurnResult(
    string ProviderName,
    ChatRequest Request,
    ChatResult Response);
