using UniversalAI.Core;

namespace UniversalAI.Services.Context;

public static class SlidingWindowMemory
{
    public static List<ChatMessage> Select(IReadOnlyList<ChatMessage> history, int keep)
    {
        if (keep < 2) throw new ArgumentOutOfRangeException(nameof(keep), "N должно быть >= 2.");
        int start = Math.Max(0, history.Count - keep);
        // Plain messages keep an exact window even for odd N. A tool turn is atomic.
        int turnStart = start;
        while (turnStart > 0 && history[turnStart].Role != "user") turnStart--;
        bool partialToolTurn = start > turnStart && history.Skip(turnStart)
            .TakeWhile((message, index) => index == 0 || message.Role != "user")
            .Any(message => message.Role == "tool" || message.ToolCalls?.Count > 0);
        if (partialToolTurn)
            while (start < history.Count && history[start].Role != "user") start++;
        return history.Skip(start).ToList();
    }
}
