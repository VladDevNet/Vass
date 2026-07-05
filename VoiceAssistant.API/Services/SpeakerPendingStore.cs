namespace VoiceAssistant.API.Services;

// In-memory tracker for an unrecognized voice we're accumulating evidence on
// across turns, before deciding it's a real distinct new speaker (not noise or
// a one-off misfire). Deliberately not DB-backed — this state is only useful
// while actively building confidence and is fine to lose on restart.
public class SpeakerPendingStore
{
    private readonly object _lock = new();
    private List<float[]> _embeddings = new();
    private List<string> _transcripts = new();

    public void Reset()
    {
        lock (_lock)
        {
            _embeddings = new List<float[]>();
            _transcripts = new List<string>();
        }
    }

    public void Add(float[] embedding, string transcript)
    {
        lock (_lock)
        {
            _embeddings.Add(embedding);
            _transcripts.Add(transcript);
        }
    }

    public (List<float[]> Embeddings, List<string> Transcripts) Snapshot()
    {
        lock (_lock)
        {
            return (new List<float[]>(_embeddings), new List<string>(_transcripts));
        }
    }

    public int Count
    {
        get { lock (_lock) return _embeddings.Count; }
    }
}
