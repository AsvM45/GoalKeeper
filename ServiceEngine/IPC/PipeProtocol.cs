using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceEngine.IPC;

// ── Message type constants ────────────────────────────────────────────────────

public static class MessageType
{
    // Service → UI
    public const string ShowFriction    = "SHOW_FRICTION";
    public const string SessionExpired  = "SESSION_EXPIRED";
    public const string StateUpdate     = "STATE_UPDATE";
    public const string DiagnosticResult = "DIAGNOSTIC_RESULT";

    // UI → Service
    public const string AllowSession    = "ALLOW_SESSION";
    public const string EnforceClose    = "ENFORCE_CLOSE";
    public const string UpdateSettings  = "UPDATE_SETTINGS";
    public const string ArmSystem       = "ARM_SYSTEM";
    public const string ActivateNuclear = "ACTIVATE_NUCLEAR";
    public const string DeactivateNuclear = "DEACTIVATE_NUCLEAR";
    public const string RunDiagnostics  = "RUN_DIAGNOSTICS";
    public const string GetState        = "GET_STATE";
    public const string SetAIKey        = "SET_AI_KEY";
    public const string OverrideAI      = "OVERRIDE_AI";
    public const string ResumeProcess   = "RESUME_PROCESS";
}

// ── Base message ──────────────────────────────────────────────────────────────

public sealed class PipeMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }

    // ── Factory methods (Service → UI) ────────────────────────────────────────

    public static PipeMessage ShowFriction(string app, string category, int delaySeconds) =>
        Create(MessageType.ShowFriction, new { app, category, delaySeconds });

    public static PipeMessage SessionExpired(string app) =>
        Create(MessageType.SessionExpired, new { app });

    public static PipeMessage StateUpdate(string mode, long nuclearEnd, bool isArmed) =>
        Create(MessageType.StateUpdate, new { mode, nuclearEnd, isArmed });

    public static PipeMessage DiagResult(bool success, string? reason) =>
        Create(MessageType.DiagnosticResult, new { success, reason });

    // ── Serialization ─────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static PipeMessage Create<T>(string type, T payload)
    {
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, _opts));
        return new PipeMessage { Type = type, Payload = doc.RootElement };
    }

    public string Serialize() => JsonSerializer.Serialize(this, _opts);

    public static PipeMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<PipeMessage>(json, _opts);

    // ── Payload helpers ───────────────────────────────────────────────────────

    public string? GetString(string key) =>
        Payload.TryGetProperty(key, out var p) ? p.GetString() : null;

    public int GetInt(string key, int defaultVal = 0) =>
        Payload.TryGetProperty(key, out var p) && p.TryGetInt32(out int v) ? v : defaultVal;

    public bool GetBool(string key, bool defaultVal = false) =>
        Payload.TryGetProperty(key, out var p) ? p.GetBoolean() : defaultVal;

    public long GetLong(string key, long defaultVal = 0) =>
        Payload.TryGetProperty(key, out var p) && p.TryGetInt64(out long v) ? v : defaultVal;
}
