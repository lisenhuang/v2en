namespace v2en.Services;

/// <summary>
/// One ChatGPT/Codex model the admin can pick for translation, with the reasoning-effort levels it
/// accepts. Effort levels differ per model, which is why they're carried here rather than assumed.
/// </summary>
public record ChatGptModel(string Id, string DisplayName, IReadOnlyList<string> ReasoningEfforts, string DefaultReasoning);

/// <summary>
/// Supplies the list of ChatGPT (Codex-backed) models available to a connected ChatGPT plan, with
/// each model's supported reasoning-effort levels. The ChatGPT backend does not expose a stable
/// public "list models" endpoint the way the platform API does, so this is a curated catalogue of the
/// current Codex model line-up. The admin UI always ALSO allows a free-text model id + effort, so the
/// admin is never blocked when OpenAI ships a new slug before this list is updated.
/// </summary>
public class ChatGptModelsService
{
    // Kept small and current. Order = suggested preference (best general translation first).
    private static readonly IReadOnlyList<ChatGptModel> Catalog = new List<ChatGptModel>
    {
        new("gpt-5.1-codex",      "GPT-5.1 Codex",      new[] { "low", "medium", "high", "xhigh" }, "medium"),
        new("gpt-5.1",            "GPT-5.1",            new[] { "minimal", "low", "medium", "high", "xhigh" }, "medium"),
        new("gpt-5.1-codex-mini", "GPT-5.1 Codex mini", new[] { "low", "medium", "high" }, "medium"),
        new("gpt-5-codex",        "GPT-5 Codex",        new[] { "low", "medium", "high" }, "medium"),
        new("gpt-5",              "GPT-5",              new[] { "minimal", "low", "medium", "high" }, "medium"),
    };

    /// <summary>All reasoning efforts we know about, used to validate free-text entries loosely.</summary>
    public static readonly IReadOnlyList<string> AllEfforts = new[] { "minimal", "low", "medium", "high", "xhigh" };

    public IReadOnlyList<ChatGptModel> GetModels() => Catalog;

    /// <summary>Look up a catalogue model by id (case-insensitive), or null for a custom/unknown id.</summary>
    public ChatGptModel? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Catalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
