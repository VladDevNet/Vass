namespace VoiceAssistant.API.Data.Entities;

// One structured log line from the mobile client, batched and shipped
// asynchronously (see mobile/src/logging/remoteLogger.ts) — for
// development-stage debugging of the voice loop (VAD/TTS/turn-taking)
// against a real device without needing adb logcat access. Message/Data are
// deliberately unbounded text at the DB level, not varchar(n): a length cap
// here would silently truncate or 500 on a long stack trace/payload for no
// real benefit (see SettingsController's DisplayName precedent for why an
// unenforced cap is worse than none). Bounded instead at the request layer
// (ClientLogsController's MaxMessageLength/MaxDataLength) -- reject-and-skip
// an oversized entry there, rather than let a DB constraint corrupt or
// crash on it here.
public class ClientLogEntry
{
    public long Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow; // server receipt time
    public DateTime ClientTimestamp { get; set; } // when it happened on-device
    public string RunId { get; set; } = null!; // random per app launch — groups one test session
    public string Level { get; set; } = null!; // debug | info | warn | error
    public string Category { get; set; } = null!; // vad | tts | mic | turn | app
    public string Message { get; set; } = null!;
    public string? DataJson { get; set; } // arbitrary structured payload, serialized
}
