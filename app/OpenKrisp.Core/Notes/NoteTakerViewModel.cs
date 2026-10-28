using OpenKrisp.Core.Common;

namespace OpenKrisp.Core.Notes;

public sealed class NoteTakerViewModel : ObservableObject, IDisposable
{
    private readonly ActionItemStore _store;
    private readonly ILocalNotesWorker _worker;
    private readonly IMeetingRecorder _recorder;
    private readonly string _recordings;
    private CancellationTokenSource? _operation;
    private Meeting? _recording;
    private ILiveTranscript? _live;
    private ILiveNotesSession? _liveNotes;
    private int _notesGeneration, _submittedParagraphs, _submittedWords;
    private DateTime _lastNotesRequest, _notesRetryAfter;
    private IReadOnlyList<TranscriptTurn> _liveStableTurns = Array.Empty<TranscriptTurn>();
    private bool _liveNotesEnabled = true, _liveNotesBusy;
    private string _liveSummary = "", _liveSummaryStatus = "Updates after a few paragraphs. Foreign phrases receive inline English translations.";
    private IReadOnlyList<string> _liveKeyPoints = Array.Empty<string>();
    private IReadOnlyList<TranscriptTranslation> _liveTranslations = Array.Empty<TranscriptTranslation>();
    public string LiveSummary { get => _liveSummary; private set => SetField(ref _liveSummary, value); }
    public string LiveSummaryStatus { get => _liveSummaryStatus; private set => SetField(ref _liveSummaryStatus, value); }
    public IReadOnlyList<string> LiveKeyPoints { get => _liveKeyPoints; private set => SetField(ref _liveKeyPoints, value); }
    public IReadOnlyList<TranscriptTranslation> LiveTranslations { get => _liveTranslations; private set => SetField(ref _liveTranslations, value); }
    public bool LiveNotesBusy { get => _liveNotesBusy; private set => SetField(ref _liveNotesBusy, value); }
    public bool LiveNotesEnabled
    {
        get => _liveNotesEnabled;
        set
        {
            if (!SetField(ref _liveNotesEnabled, value)) return;
            StopLiveNotes();
            _submittedParagraphs = _submittedWords = 0;
            _lastNotesRequest = _notesRetryAfter = default;
            LiveSummaryStatus = value ? "Waiting for a few paragraphs to update notes and translations…" : "Paused · your last summary and translations are kept.";
            if (value) QueueRunningNotes();
        }
    }
    private readonly IProgress<AudioLevel> _levels;
    private double _peakDb = -60, _rmsDb = -60, _heldPeakDb = -60;
    private double _speechProbabilityPercent, _turnProbabilityPercent;
    private DateTime _peakUntil, _detectionAt;
    private bool _speechDetected, _turnComplete;
    public double PeakDb => _peakDb;
    public double RmsDb => _rmsDb;
    public double HeldPeakDb => _heldPeakDb;
    public string LevelText => $"Peak {_peakDb:F1} dBFS · RMS {_rmsDb:F1} dBFS · held peak {_heldPeakDb:F1} dBFS";
    public string SpeechDetectionText => !IsRecording ? "Speech detection idle" : _detectionAt == default ? "Waiting for speech analysis…" :
        (DateTime.UtcNow - _detectionAt).TotalSeconds > 10 ? "Waiting for the next speech analysis…" :
        _speechDetected ? "Speech detected in latest analyzed audio" : "No speech in latest analyzed audio";
    public string ProcessingStep => IsRecording ? "1  Capture live audio → 2  Detect speech → 3  Transcribe and identify speakers" :
        IsBusy ? Status : "Ready to record → detect speech → transcribe → summarize";
    public double SpeechProbabilityPercent => _speechProbabilityPercent;
    public double TurnProbabilityPercent => _turnProbabilityPercent;
    public string SpeechConfidenceText => $"Voice activity: {_speechProbabilityPercent:F0}% speech likelihood · automatic";
    public string TurnConfidenceText => $"Smart Turn: {_turnProbabilityPercent:F0}% turn-complete confidence · automatic";
    public string TurnDetectionText => !IsRecording ? "Turn detection idle" : _detectionAt == default ? "Listening for a complete thought…" :
        _speechDetected ? "Speaking · listening" : _turnComplete ? "Thought complete" : "Continuing · waiting for the rest of the thought";
    public void RefreshModelStatus() => Refresh();
    public IReadOnlyList<RecordingInput> Inputs { get; }
    public RecordingInput SelectedInput { get; set; }
    public IReadOnlyList<RecordingSource> AudioSources { get; }
    public RecordingSource SelectedAudioSource
    {
        get => AudioSources.FirstOrDefault(s => s.Mode == RecordingSourceIndex && (s.Mode == 2 || s.InputId == SelectedInput.Id)) ?? AudioSources[0];
        set
        {
            if (value is null || !AudioSources.Contains(value) || IsRecording || IsBusy) return;
            if (RecordingSourceIndex == value.Mode && (value.Mode == 2 || SelectedInput.Id == value.InputId)) return;
            RecordingSourceIndex = value.Mode;
            SelectedInput = Inputs.FirstOrDefault(i => i.Id == value.InputId) ?? Inputs[0];
            OnPropertyChanged();
        }
    }
    private string _liveText = "";
    public string LiveText { get => _liveText; private set => SetField(ref _liveText, value); }
    private IReadOnlyList<TranscriptTurn> _liveTurns = Array.Empty<TranscriptTurn>();
    public IReadOnlyList<TranscriptTurn> LiveTurns { get => _liveTurns; private set => SetField(ref _liveTurns, value); }
    private string _liveStatus = "";
    public string LiveStatus { get => _liveStatus; private set => SetField(ref _liveStatus, value); }
    private bool _busy;
    private string _search = "";
    private string _tag = "";
    private int _kindFilter;
    public string NewTags { get; set; } = "";
    public int NewKindIndex { get; set; }
    public string Search { get => _search; set { if (SetField(ref _search, value)) Rebuild(); } }
    public string TagFilter { get => _tag; set { if (SetField(ref _tag, value)) Rebuild(); } }
    public int KindFilterIndex { get => _kindFilter; set { if (SetField(ref _kindFilter, value)) Rebuild(); } }
    private string _status = "Record a meeting or import audio. Summarize each transcript when you are ready.";
    private string _error = "";
    private IReadOnlyList<TranscriptCard> _transcripts = Array.Empty<TranscriptCard>();
    private TranscriptCard? _selectedTranscript;
    public TranscriptCard? SelectedTranscript { get => _selectedTranscript; set => SetField(ref _selectedTranscript, value); }

    public NoteTakerViewModel(ActionItemStore store, ILocalNotesWorker worker, IMeetingRecorder recorder, string? recordings = null)
    {
        _store = store; _worker = worker; _recorder = recorder;
        Inputs = new[] { new RecordingInput("", "Default microphone") }.Concat(recorder.Inputs).ToList();
        var preferred = Environment.GetEnvironmentVariable("OPENKRISP_RECORDING_INPUT");
        SelectedInput = Inputs.FirstOrDefault(i => preferred is not null && i.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase))
            ?? Inputs.FirstOrDefault(i => i.Name.StartsWith("Voicemeeter Out B1", StringComparison.OrdinalIgnoreCase)) ?? Inputs[0];
        AudioSources = Inputs.OrderByDescending(i => i == SelectedInput).Select(i => new RecordingSource(0, i.Id, i.Name))
            .Concat(new[] { new RecordingSource(2, "", "Computer audio (speakers, YouTube, calls)"),
                new RecordingSource(1, "", "Default microphone + computer audio") }).ToList();
        _recordings = recordings ?? Path.Combine(AppPaths.DataDir, "recordings");
        _levels = new Progress<AudioLevel>(UpdateLevel);
        _recorder.LevelAvailable += OnLevel;
        _recorder.SamplesAvailable += OnSamples;
        _store.Changed += Rebuild;
        Rebuild();
    }
    public bool IsBusy { get => _busy; private set { if (SetField(ref _busy, value)) Refresh(); } }
    public bool IsRecording => _recording is not null;
    public bool ModelsReady => _worker.IsReady;
    public bool CanSetUp => !IsBusy && !IsRecording && !_worker.IsPreparing;
    public bool CanStart => !IsBusy && !IsRecording && ModelsReady && !_worker.IsPreparing;
    public bool CanStop => IsRecording && !IsBusy;
    public bool CanCancel => IsBusy;
    public string ModelStatus => ModelsReady ? "Parakeet Ultra ASR on CUDA · Smart Turn on Tract CUDA · Supra titles · detailed Qwen2.5-7B-Instruct-1M notes" :
        _worker.RuntimeStatus.Length > 0 ? _worker.RuntimeStatus : "Preparing local models. Open Status for download and model controls.";
    public string Status { get => _status; private set { if (SetField(ref _status, value)) OnPropertyChanged(nameof(ProcessingStep)); } }
    public string Error { get => _error; private set => SetField(ref _error, value); }
    public IReadOnlyList<TranscriptCard> Transcripts { get => _transcripts; private set => SetField(ref _transcripts, value); }
    public int ExpectedSpeakers { get; set; } = 2;
    public int RecordingSourceIndex { get; set; }

    public Task SetUpAsync() => RunAsync(async (progress, ct) => {
        await _worker.InstallAsync(progress, ct);
        Status = "Local models are ready.";
    });

    public Task ImportAsync(string path, string title) => RunAsync(async (progress, ct) => {
        LiveTurns = Array.Empty<TranscriptTurn>();
        LiveText = "";
        LiveStatus = "";
        Directory.CreateDirectory(_recordings);
        var savedAudio = Path.Combine(_recordings, Guid.NewGuid() + Path.GetExtension(path));
        var temporary = savedAudio + ".partial";
        try
        {
            await using (var input = File.OpenRead(path))
            await using (var output = File.Create(temporary))
                await input.CopyToAsync(output, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, savedAudio);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var meeting = _store.AddMeeting(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title);
        _store.SetOrganization(meeting.Id, NewKindIndex == 1 ? "Lecture" : "Meeting", NewTags);
        _store.SaveTranscript(meeting.Id, Array.Empty<TranscriptTurn>(), savedAudio);
        var turns = await _worker.TranscribeAsync(savedAudio, ExpectedSpeakers, progress, ct);
        ct.ThrowIfCancellationRequested();
        _store.SaveTranscript(meeting.Id, turns, savedAudio);
        if (turns.Count > 0) _store.SetTitle(meeting.Id, await _worker.CreateTitleAsync(turns, progress, ct));
        Status = turns.Count == 0 ? "No speech detected in this recording." : "Transcript saved. Click Summarize above it to generate key points and action items.";
    });

    public async Task StartRecordingAsync(string title)
    {
        if (!CanStart) return;
        Error = "";
        IsBusy = true;
        try
        {
            var path = Path.Combine(_recordings, Guid.NewGuid() + ".wav");
            Directory.CreateDirectory(_recordings);
            LiveText = "";
            LiveTurns = Array.Empty<TranscriptTurn>();
            LiveStatus = "Starting live speaker transcription…";
            StopLiveNotes();
            LiveSummary = ""; LiveKeyPoints = Array.Empty<string>(); LiveTranslations = Array.Empty<TranscriptTranslation>();
            _submittedParagraphs = _submittedWords = 0; _lastNotesRequest = _notesRetryAfter = default;
            _liveStableTurns = Array.Empty<TranscriptTurn>();
            LiveSummaryStatus = LiveNotesEnabled ? "Updates after a few paragraphs. Foreign phrases receive inline English translations." : "Running notes paused.";
            _detectionAt = default;
            ResetMeter();
            _live = _worker.StartLive(new Progress<LiveTranscriptUpdate>(update => {
                if (!IsRecording && !IsBusy) return;
                if (update.Error.Length > 0) { LiveStatus = update.Error; return; }
                _speechDetected = update.SpeechDetected;
                _turnComplete = update.TurnComplete;
                _speechProbabilityPercent = Math.Clamp(update.SpeechProbability*100,0,100);
                _turnProbabilityPercent = Math.Clamp(update.TurnProbability*100,0,100);
                foreach (var property in new[] { nameof(SpeechProbabilityPercent), nameof(TurnProbabilityPercent), nameof(SpeechConfidenceText), nameof(TurnConfidenceText), nameof(TurnDetectionText) }) OnPropertyChanged(property);
                _detectionAt = DateTime.UtcNow;
                OnPropertyChanged(nameof(SpeechDetectionText));
                if (_recording is not null)
                {
                    try { _store.UpdateLiveInsights(_recording.Id, update.Turns.Where(t => t.End <= update.AudioEnd - 1).ToList()); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Error = "Could not save extracted items: " + error.Message; }
                }
                LiveTurns = TranscriptParagraphs.Combine(update.Turns);
                _liveStableTurns = TranscriptParagraphs.Combine(update.Turns.Where(t => t.End <= update.AudioEnd - 1).ToList());
                if (LiveNotesBusy && update.LatencySeconds > 12)
                {
                    StopLiveNotes(); _notesRetryAfter = DateTime.UtcNow.AddSeconds(45);
                    _submittedParagraphs = _submittedWords = 0;
                    LiveSummaryStatus = "Giving transcription time to catch up · the last summary is kept.";
                }
                QueueRunningNotes();
                LiveText = string.Join(" ", update.Turns.Select(t => t.Speaker + ": " + t.Text));
                LiveStatus = $"Live speaker transcript · about {update.LatencySeconds:F1}s behind audio" +
                    (update.SkippedSeconds > 0 ? " · preview caught up; final transcript includes all recorded audio" : "");
            }), ExpectedSpeakers);
            await _recorder.StartAsync(path, RecordingSourceIndex, SelectedInput.Id);
            _recording = _store.AddMeeting(string.IsNullOrWhiteSpace(title) ? $"Meeting {DateTime.Now:g}" : title);
            _store.SetOrganization(_recording.Id, NewKindIndex == 1 ? "Lecture" : "Meeting", NewTags);
            RecordingPath = path;
            _store.SaveTranscript(_recording.Id, Array.Empty<TranscriptTurn>(), path);
            SelectedTranscript = Transcripts.FirstOrDefault(t => t.Id == _recording.Id);
            Status = RecordingSourceIndex switch { 1 => "Recording microphone and system audio…", 2 => "Recording speaker playback…", _ => "Recording microphone…" };
        }
        catch (Exception e) { StopLiveNotes(); _live?.Dispose(); _live = null; _recorder.Dispose(); _recording = null; Error = e.Message; }
        finally { IsBusy = false; Refresh(); }
    }
    private string RecordingPath { get; set; } = "";

    public Task StopRecordingAsync()
    {
        if (!CanStop) return Task.CompletedTask;
        StopLiveNotes();
        var meeting = _recording!;
        return RunAsync(async (progress, ct) => {
            try
            {
                await _recorder.StopAsync();
                if (_live is not null) await _live.CompleteAsync();
            }
            finally
            {
                _live?.Dispose();
                _live = null;
                _recording = null;
                ResetMeter();
                Refresh();
            }
            LiveStatus = "Recording stopped; finalizing the transcript and speaker labels…";
            Status = "Recording saved. Transcribing locally…";
            var turns = await _worker.TranscribeAsync(RecordingPath, ExpectedSpeakers, progress, ct);
            ct.ThrowIfCancellationRequested();
            _store.SaveTranscript(meeting.Id, turns, RecordingPath);
            LiveStatus = "Final transcript saved below.";
            if (turns.Count > 0) _store.SetTitle(meeting.Id, await _worker.CreateTitleAsync(turns, progress, ct));
            Status = turns.Count == 0 ? "No speech detected. Your recording was saved." : "Transcript saved. Click Summarize above it when ready.";
        }, allowWhileRecording: true);
    }

    public void SaveTags(Guid meetingId, string tags)
    {
        var meeting = _store.Meetings.Single(m => m.Id == meetingId);
        _store.SetOrganization(meetingId, meeting.Kind, tags);
    }

    public Task TranscribeMeetingAsync(Guid meetingId) => RunAsync(async (progress, ct) => {
        var meeting = _store.Meetings.Single(m => m.Id == meetingId);
        var turns = await _worker.TranscribeAsync(meeting.AudioPath, ExpectedSpeakers, progress, ct);
        ct.ThrowIfCancellationRequested();
        _store.SaveTranscript(meetingId, turns, meeting.AudioPath);
        if (turns.Count > 0) _store.SetTitle(meetingId, await _worker.CreateTitleAsync(turns, progress, ct));
        Status = "Transcript saved. Click Summarize when ready.";
    });

    public Task SummarizeAsync(Guid meetingId) => RunAsync(async (progress, ct) => {
        var meeting = _store.Meetings.Single(m => m.Id == meetingId);
        var revision = meeting.TranscriptRevision;
        var summary = await _worker.SummarizeAsync(meeting.Transcript.ToList(), progress, ct);
        ct.ThrowIfCancellationRequested();
        _store.SaveSummary(meetingId, revision, summary);
        Status = "Key points and action items saved for " + meeting.Title + ".";
    });

    public void Cancel() => _operation?.Cancel();

    private async Task RunAsync(Func<IProgress<string>, CancellationToken, Task> operation, bool allowWhileRecording = false)
    {
        if (IsBusy || (IsRecording && !allowWhileRecording)) return;
        using var source = new CancellationTokenSource();
        _operation = source;
        IsBusy = true;
        Error = "";
        var progress = new Progress<string>(message => { if (ReferenceEquals(_operation, source) && !source.IsCancellationRequested && IsBusy) Status = message; });
        try { await operation(progress, source.Token); }
        catch (OperationCanceledException) { Status = "Processing cancelled. Saved audio and transcripts are kept."; }
        catch (Exception e) { Error = e.Message; Status = "Processing failed. Saved audio and transcripts are kept."; }
        finally { _operation = null; IsBusy = false; Refresh(); }
    }

    private void Rebuild()
    {
        var selected = SelectedTranscript?.Id;
        Transcripts = _store.Meetings.Where(m => m.Transcript.Count > 0 || m.AudioPath.Length > 0)
        .Where(m => KindFilterIndex == 0 || m.Kind == (KindFilterIndex == 1 ? "Meeting" : "Lecture"))
        .Where(m => string.IsNullOrWhiteSpace(TagFilter) || m.Tags.Any(t => t.Contains(TagFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
        .Where(m => string.IsNullOrWhiteSpace(Search) || SearchText(m).Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(m => m.StartedAt)
        .Select(m => new TranscriptCard {
            Meeting = m, CanSummarize = CanStart && m.Transcript.Count > 0, CanTranscribe = CanStart && File.Exists(m.AudioPath),
            ActionItems = _store.Items.Where(i => i.MeetingId == m.Id).OrderBy(i => i.Done).ThenBy(i => i.CapturedAt)
                .Select(i => new ActionItemView(i, _store, () => DateOnly.FromDateTime(DateTime.Now))).ToList(),
        }).ToList();
        SelectedTranscript = Transcripts.FirstOrDefault(t => t.Id == selected) ?? Transcripts.FirstOrDefault();
    }

    private string SearchText(Meeting meeting) => string.Join("\n", new[] {
        meeting.Title, meeting.Summary, meeting.RunningSummary, string.Join(" ", meeting.Tags), string.Join(" ", meeting.KeyPoints),
        string.Join(" ", meeting.RunningKeyPoints), string.Join(" ", meeting.Translations.Select(t => t.Text + " " + t.Translation)),
        string.Join(" ", meeting.Decisions), string.Join(" ", meeting.OpenQuestions), string.Join(" ", meeting.Risks),
        string.Join(" ", meeting.Topics.Select(t => t.Title + " " + string.Join(" ", t.Points))),
        string.Join(" ", meeting.Transcript.Select(t => t.Speaker + " " + t.Text)),
        string.Join(" ", _store.Items.Where(i => i.MeetingId == meeting.Id).Select(i => i.Text + " " + i.Assignee)),
    });

    private void Refresh()
    {
        foreach (var property in new[] { nameof(IsRecording), nameof(ModelsReady), nameof(CanSetUp), nameof(CanStart), nameof(CanStop), nameof(CanCancel), nameof(ModelStatus), nameof(ProcessingStep), nameof(SpeechDetectionText), nameof(TurnDetectionText) })
            OnPropertyChanged(property);
        Rebuild();
    }

    private void OnSamples(float[] samples) => _live?.AddSamples(samples);
    private void OnLevel(AudioLevel level) => _levels.Report(level);

    private void QueueRunningNotes()
    {
        if (!LiveNotesEnabled || !IsRecording || IsBusy || DateTime.UtcNow < _notesRetryAfter
            || DateTime.UtcNow - _lastNotesRequest < TimeSpan.FromSeconds(30)) return;
        var stable = _liveStableTurns;
        // Exclude tentative words, while allowing a long paragraph to start notes
        // without waiting for a speaker change.
        if (stable.Count == 0) return;
        var words = stable.Sum(t => t.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        if (stable.Count - _submittedParagraphs < 3 && words - _submittedWords < 180) return;
        if (words < 35) return;
        if (_liveNotes is null)
        {
            var generation = ++_notesGeneration;
            var meetingId = _recording!.Id;
            try
            {
                _liveNotes = _worker.StartLiveNotes(new Progress<LiveNotesUpdate>(update => {
                    if (generation != _notesGeneration || _recording?.Id != meetingId) return;
                    LiveNotesBusy = false;
                    if (update.Error.Length > 0)
                    {
                        StopLiveNotes(); _notesRetryAfter = DateTime.UtcNow.AddMinutes(1);
                        _submittedParagraphs = _submittedWords = 0;
                        LiveSummaryStatus = "Notes will retry; transcription continues. " + update.Error;
                        return;
                    }
                    LiveSummary = update.Summary; LiveKeyPoints = update.KeyPoints; LiveTranslations = update.Translations;
                    LiveSummaryStatus = $"Rewritten through {TimeSpan.FromSeconds(update.ThroughSeconds):hh\\:mm\\:ss} · provisional, updated as context grows";
                    try { _store.SaveRunningNotes(meetingId, update); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LiveSummaryStatus = "Could not save running notes: " + error.Message; }
                }), new Progress<string>(message => {
                    if (generation != _notesGeneration || _recording?.Id != meetingId) return;
                    LiveNotesBusy = true; LiveSummaryStatus = message;
                }));
            }
            catch (Exception error)
            {
                _notesRetryAfter = DateTime.UtcNow.AddMinutes(1);
                LiveSummaryStatus = "Notes unavailable; transcription continues. " + error.Message;
                return;
            }
        }
        if (_liveNotes is null) return;
        _liveNotes.Submit(stable);
        _submittedParagraphs = stable.Count; _submittedWords = words; _lastNotesRequest = DateTime.UtcNow;
        LiveNotesBusy = true; LiveSummaryStatus = "Rewriting the summary with the latest context and checking foreign phrases…";
    }

    private void StopLiveNotes()
    {
        _notesGeneration++;
        _liveNotes?.Dispose(); _liveNotes = null; LiveNotesBusy = false;
    }

    private void ResetMeter()
    {
        _heldPeakDb = -60;
        _peakUntil = default;
        _speechProbabilityPercent = _turnProbabilityPercent = 0;
        foreach (var property in new[] { nameof(SpeechProbabilityPercent), nameof(TurnProbabilityPercent), nameof(SpeechConfidenceText), nameof(TurnConfidenceText), nameof(TurnDetectionText) }) OnPropertyChanged(property);
        UpdateLevel(AudioLevel.Silence);
    }

    private void UpdateLevel(AudioLevel level)
    {
        _peakDb = IsRecording ? level.PeakDb : -60;
        _rmsDb = IsRecording ? level.RmsDb : -60;
        var now = DateTime.UtcNow;
        if (_peakDb >= _heldPeakDb) { _heldPeakDb = _peakDb; _peakUntil = now.AddSeconds(1); }
        else if (now > _peakUntil) _heldPeakDb = Math.Max(_peakDb, _heldPeakDb - 1.5);
        foreach (var property in new[] { nameof(PeakDb), nameof(RmsDb), nameof(HeldPeakDb), nameof(LevelText), nameof(SpeechDetectionText), nameof(TurnDetectionText) }) OnPropertyChanged(property);
    }

    public void Dispose()
    {
        StopLiveNotes();
        _recorder.SamplesAvailable -= OnSamples;
        _recorder.LevelAvailable -= OnLevel;
        _live?.Dispose();
        _operation?.Cancel();
        _store.Changed -= Rebuild;
        _recorder.Dispose();
    }
}
