using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalAI.Core;
using UniversalAI.Data;
using UniversalAI.Data.Entities;
using UniversalAI.Providers;
using UniversalAI.Services;
using UniversalAI.Services.Context;

namespace UniversalAI.Benchmarks;

public enum ContextStrategy { SlidingWindow, StickyFacts, Summary }

public sealed class Day10MemoryState
{
    public ContextStrategy Strategy { get; set; }
    public int Keep { get; set; } = 6;
    public int Batch { get; set; } = 10;
    public string Model { get; set; } = "openai/gpt-4o-mini";
    public string SystemPrompt { get; set; } = Day9CompressionScenario.SystemPrompt;
    public List<ChatMessage> History { get; set; } = [];
    public Dictionary<string, string> Facts { get; set; } = [];
    public ContextSummaryEntity? Summary { get; set; }
}

/// <summary>Benchmark agent. Snapshots include the archive but only selected context reaches the API.</summary>
public sealed class Day10ContextSession(Day10MemoryState state, IAIProvider provider)
{
    public Day10MemoryState State { get; } = state;

    // Strings are immutable; restoring always creates independent mutable objects.
    public string Checkpoint() => JsonSerializer.Serialize(State);
    public static Day10ContextSession Restore(string checkpoint, IAIProvider provider) =>
        new(JsonSerializer.Deserialize<Day10MemoryState>(checkpoint)
            ?? throw new InvalidOperationException("Пустой checkpoint."), provider);

    private async Task UpdateFactsAsync(string user, CancellationToken cancellationToken)
    {
        if (State.Strategy == ContextStrategy.StickyFacts)
            State.Facts = await StickyFactsMemory.UpdateAsync(State.Facts,
                SlidingWindowMemory.Select(State.History, State.Keep), user, State.Model, provider, cancellationToken);
    }

    public async Task ReplayAsync(ChatMessage user, ChatMessage assistant, CancellationToken cancellationToken = default)
    {
        await UpdateFactsAsync(user.Content, cancellationToken);
        State.History.AddRange([Clone(user), Clone(assistant)]);
        await PrepareAsync(cancellationToken: cancellationToken);
    }

    public async Task<List<ChatMessage>> PrepareAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (State.Keep < 2 || State.Batch < 1) throw new ArgumentException("N >= 2, batch >= 1.");
        if (State.Strategy == ContextStrategy.SlidingWindow)
            return SlidingWindowMemory.Select(State.History, State.Keep);
        if (State.Strategy == ContextStrategy.StickyFacts)
            return [StickyFactsMemory.AsMessage(State.Facts), .. SlidingWindowMemory.Select(State.History, State.Keep)];

        // Reuse the same algorithm against this snapshot, without creating a temporary database.
        var conversation = new ConversationEntity
        {
            Id = 1, Model = State.Model, ContextKeepLastMessages = State.Keep,
            ContextCompressionBatchSize = State.Batch
        };
        var prepared = await new ContextCompressionService(new SnapshotMemoryStore(State)).PrepareAsync(
            conversation, State.History, provider, cancellationToken, force);
        return prepared.Messages;
    }

    public async Task<ChatResult> AskAsync(string text, bool remember = true, CancellationToken cancellationToken = default)
    {
        await UpdateFactsAsync(text, cancellationToken);
        var context = await PrepareAsync(cancellationToken: cancellationToken);
        ChatMessage user = new() { Role = "user", Content = text };
        var response = await provider.SendMessageAsync(new ChatRequest
        {
            Model = State.Model, Temperature = 0, MaxTokens = 240,
            Messages = [new() { Role = "system", Content = State.SystemPrompt }, .. context, user]
        }, cancellationToken);
        StickyFactsMemory.EnsureComplete(response);
        if (remember) State.History.AddRange([user, new() { Role = "assistant", Content = response.Content }]);
        return response;
    }

    private static ChatMessage Clone(ChatMessage message) =>
        JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message))!;
    private sealed class SnapshotMemoryStore(Day10MemoryState state) : IContextMemoryStore
    {
        public Task<ContextSummaryEntity?> GetSummaryAsync(int id, CancellationToken ct = default) => Task.FromResult(state.Summary);
        public Task SaveSummaryAsync(int id, string content, int covered, ChatResult usage, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var summary = state.Summary ??= new ContextSummaryEntity { ConversationId = id };
            summary.Content = content; summary.CoveredMessageCount = covered; summary.Revision++;
            summary.PromptTokens += usage.PromptTokens; summary.CompletionTokens += usage.CompletionTokens;
            summary.UsageComplete &= usage.PromptTokens > 0 || usage.CompletionTokens > 0;
            return Task.CompletedTask;
        }
        public Task SaveFactsAsync(ConversationEntity conversation, Dictionary<string, string> facts, CancellationToken ct = default, ChatResult? usage = null)
        { state.Facts = new(facts); return Task.CompletedTask; }
    }
}
