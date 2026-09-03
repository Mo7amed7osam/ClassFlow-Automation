using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

public sealed class AiMatchingViewModel : ObservableObject, IDisposable
{
    private readonly IAiCredentialStore _store;
    private readonly IAiMatchingService _service;
    private readonly IAiModelCatalog _catalog;
    private readonly Dictionary<AiProvider, List<string>> _addedModels = new();
    private CancellationTokenSource? _operation;
    private bool _idle = true, _ready, _consent, _hasSavedKey;
    private string _severity = "Info";
    private string _model = "gpt-5.4-mini", _status = "Enter an OpenAI API key, then Test & save securely.", _names = "";
    private RosterGroup? _group;
    private AiProvider _provider = AiProvider.OpenAI;
    public IReadOnlyList<AiProvider> Providers { get; } = [AiProvider.OpenAI, AiProvider.OpenRouter];
    public AiProvider Provider
    {
        get => _provider;
        set
        {
            if (!IsIdle || !Enum.IsDefined(value) || !SetProperty(ref _provider, value)) return;
            IsReady = false;
            AllowExternalMatching = false;
            OnPropertyChanged(nameof(Models));
            OnPropertyChanged(nameof(ConsentText));
            OnPropertyChanged(nameof(ProviderHelp));
            OnPropertyChanged(nameof(ConnectionLabel));
            Model = Models[0];
            OnPropertyChanged(nameof(CanRemoveModel));
            try { HasSavedKey = _store.Read()?.Provider == value; }
            catch { HasSavedKey = false; }
            Status = $"Selected {value}. Enter its key and test the selected model.";
        }
    }
    public string ConsentText => Provider == AiProvider.OpenRouter
        ? "Allow uncertain roster/Zoom names to be sent through OpenRouter to the selected model provider (API charges may apply)."
        : "Allow uncertain roster/Zoom names to be sent to OpenAI for this matching run (API charges may apply).";
    public string ProviderHelp => Provider == AiProvider.OpenRouter
        ? "OpenRouter • Use a full model ID, e.g. openai/gpt-4.1-mini. A model with structured JSON support is required."
        : "OpenAI • Direct OpenAI API key and model ID.";
    public AiMatchingViewModel(IAiCredentialStore store, IAiMatchingService service, IAiModelCatalog? catalog = null)
    {
        _store = store; _service = service; _catalog = catalog ?? new AiModelCatalog();
        TestSavedCommand = new AsyncRelayCommand(_ => TestAndSaveAsync(null));
        RemoveKeyCommand = new RelayCommand(_ => RemoveKey());
        AddModelCommand = new RelayCommand(_ => AddModel());
        RemoveModelCommand = new RelayCommand(_ => RemoveModel());
        MatchCommand = new AsyncRelayCommand(_ => MatchAsync());
        CancelCommand = new RelayCommand(_ => _operation?.Cancel());
    }
    public string Model { get => _model; set { if (IsIdle && SetProperty(ref _model, value)) { IsReady = false; OnPropertyChanged(nameof(CanRemoveModel)); OnPropertyChanged(nameof(ConnectionLabel)); Status = "Model changed. Test the connection before matching."; } } }
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) Severity = Classify(value); } }
    /// <summary>Info / Success / Warning / Error — the colour the status line is shown in.</summary>
    public string Severity { get => _severity; private set => SetProperty(ref _severity, value); }
    private static string Classify(string status)
    {
        if (status.StartsWith("Done", StringComparison.Ordinal) || status.Contains("verified", StringComparison.OrdinalIgnoreCase)) return "Success";
        if (status.Contains("warning", StringComparison.OrdinalIgnoreCase)) return "Warning";
        return status.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("not valid", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Cannot", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Wrong provider", StringComparison.OrdinalIgnoreCase)
            || status.Contains("stopped", StringComparison.OrdinalIgnoreCase)
            || status.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Not valid", StringComparison.Ordinal) ? "Error" : "Info";
    }
    public bool IsIdle { get => _idle; private set => SetProperty(ref _idle, value); }
    public bool IsReady { get => _ready; private set { if (SetProperty(ref _ready, value)) OnPropertyChanged(nameof(ConnectionLabel)); } }
    public bool HasSavedKey { get => _hasSavedKey; private set { if (SetProperty(ref _hasSavedKey, value)) OnPropertyChanged(nameof(ConnectionLabel)); } }
    /// <summary>The banner at the top of the page: connected, saved-but-unverified, or nothing stored yet.</summary>
    public string ConnectionLabel => IsReady
        ? $"Connected — {Provider} / {Model}"
        : HasSavedKey ? $"Saved {Provider} key — not verified yet. Click Test connection." : "Not connected — enter an API key and test it.";
    public static IReadOnlyList<string> BuiltInModels(AiProvider provider) => provider == AiProvider.OpenRouter
        ? ["openai/gpt-4.1-mini", "openai/gpt-5.4-mini", "openai/gpt-4.1"]
        : ["gpt-5.4-mini", "gpt-4.1-mini", "gpt-4.1"];
    public IReadOnlyList<string> Models
    {
        get
        {
            var models = new List<string>(BuiltInModels(Provider));
            models.AddRange(AddedModels(Provider).Where(model => !models.Contains(model, StringComparer.OrdinalIgnoreCase)));
            return models;
        }
    }
    /// <summary>Only a model the user added here can be removed; the built-in IDs stay.</summary>
    public bool CanRemoveModel => AddedModels(Provider).Contains(Model?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
    public bool AllowExternalMatching { get => _consent; set => SetProperty(ref _consent, value); }
    public string ObservedNames { get => _names; set => SetProperty(ref _names, value); }
    public RosterGroup? SelectedGroup { get => _group; set { if (SetProperty(ref _group, value)) ClearResults(); } }
    public string ConfidenceSummary => Results.Any(r => r.Status == AttendanceMatchStatus.Present)
        ? $"{Results.Where(r => r.Status == AttendanceMatchStatus.Present).Average(r => r.Confidence):0.#}%" : "—";
    public void ClearResults() { Results.Clear(); Review.Clear(); OnPropertyChanged(nameof(ConfidenceSummary)); }
    public ObservableCollection<StudentMatchResult> Results { get; } = [];
    public ObservableCollection<AttendanceReviewItem> Review { get; } = [];
    public AsyncRelayCommand TestSavedCommand { get; }
    public RelayCommand RemoveKeyCommand { get; }
    public AsyncRelayCommand MatchCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddModelCommand { get; }
    public RelayCommand RemoveModelCommand { get; }

    private List<string> AddedModels(AiProvider provider)
    {
        if (_addedModels.TryGetValue(provider, out var models)) return models;
        List<string> saved;
        // Read during data binding: an unreadable file degrades to the built-in models instead of throwing at the UI.
        try { saved = [.. _catalog.Read(provider)]; }
        catch { saved = []; }
        _addedModels[provider] = saved;
        return saved;
    }

    /// <summary>Keeps a typed model ID in the picker for next time. Nothing is sent and no key is touched.</summary>
    private void AddModel()
    {
        if (!IsIdle) return;
        string model = Model?.Trim() ?? "";
        if (!AiModelCatalog.IsValidModel(model)) { Status = "Type a model ID first — no spaces, at most 100 characters."; return; }
        if (Provider == AiProvider.OpenRouter && !model.Contains('/'))
        { Status = "OpenRouter requires a full model ID, e.g. openai/gpt-4.1-mini. Model not added."; return; }
        if (Models.Contains(model, StringComparer.OrdinalIgnoreCase)) { Status = $"{model} is already in the {Provider} list."; return; }
        var added = AddedModels(Provider);
        if (added.Count >= AiModelCatalog.MaxModels)
        { Status = $"The list already holds {AiModelCatalog.MaxModels} added models. Remove one first."; return; }
        added.Add(model);
        if (!TrySaveModels(added)) { added.Remove(model); return; }
        OnPropertyChanged(nameof(Models));
        OnPropertyChanged(nameof(CanRemoveModel));
        Status = $"Added {model} to the {Provider} list. Test the connection before matching.";
    }

    private void RemoveModel()
    {
        if (!IsIdle) return;
        string model = Model?.Trim() ?? "";
        var added = AddedModels(Provider);
        int index = added.FindIndex(candidate => string.Equals(candidate, model, StringComparison.OrdinalIgnoreCase));
        if (index < 0) { Status = "Only a model you added yourself can be removed."; return; }
        string removed = added[index];
        added.RemoveAt(index);
        if (!TrySaveModels(added)) { added.Insert(index, removed); return; }
        OnPropertyChanged(nameof(Models));
        Model = Models[0];
        OnPropertyChanged(nameof(CanRemoveModel));
        Status = $"Removed {removed} from the {Provider} list.";
    }

    private bool TrySaveModels(List<string> models)
    {
        try { _catalog.Save(Provider, models); return true; }
        catch { Status = "Cannot save the model list on this PC. The list was left unchanged."; return false; }
    }
    public void NewKeyEntered()
    {
        if (!IsIdle) return;
        IsReady = false;
        Status = "New key entered — click Test connection to validate it against the selected model.";
    }

    public void LoadSavedSettings()
    {
        try
        {
            var settings = _store.Read();
            HasSavedKey = settings != null;
            if (settings != null)
            {
                Provider = settings.Provider;
                Model = settings.Model;
                // The key was verified against this model before it was stored, so matching stays ready across restarts.
                IsReady = true;
                Status = $"Done — saved {Provider} key found for {settings.Model}. Matching is ready; test again only if the key changed.";
            }
        }
        catch { Status = "Cannot load the saved AI key. Windows Credential Manager may be unavailable."; }
    }

    // The password is passed once from PasswordBox, never bound, logged or re-displayed.
    public async Task TestAndSaveAsync(string? enteredKey)
    {
        if (!IsIdle) return;
        Begin(); IsReady = false;
        try
        {
            var saved = string.IsNullOrWhiteSpace(enteredKey) ? _store.Read() : null;
            if (saved != null && saved.Provider != Provider)
            { Status = $"Saved key belongs to {saved.Provider}, not {Provider}. Enter a key for the selected provider."; return; }
            var key = string.IsNullOrWhiteSpace(enteredKey) ? saved?.ApiKey : enteredKey.Trim();
            if (string.IsNullOrWhiteSpace(key)) { Status = "No key to test: enter a key in the API key field. No saved key exists."; return; }
            if (key.Length > 1800 || key.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(Model) || Model.Length > 100)
            { Status = "Not valid — check key/model format. No request was sent."; return; }
            if (Provider == AiProvider.OpenAI && key.StartsWith("sk-or-", StringComparison.Ordinal))
            { Status = "Wrong provider — this is an OpenRouter key. Select OpenRouter, then enter the key again. No request was sent."; return; }
            if (Provider == AiProvider.OpenRouter && !Model.Contains('/'))
            { Status = "OpenRouter requires a full model ID, e.g. openai/gpt-4.1-mini. No request was sent."; return; }
            var settings = new AiConnectionSettings(Model.Trim(), key, Provider);
            Status = $"Testing {Provider} / {settings.Model} → sending a synthetic request → waiting for a real response…";
            await _service.TestAsync(settings, _operation!.Token);
            _operation.Token.ThrowIfCancellationRequested();
            Status = "Connection verified → saving securely…";
            _store.Save(settings);
            HasSavedKey = true;
            IsReady = true;
            Status = $"Done — Valid. {Provider} / {settings.Model} returned a completed, validated JSON response. Key saved securely; matching is ready.";
            ConsoleLogger.Info("[AI_SETUP] Done: connection verified and credential saved.");
        }
        catch (Exception ex) { Status = AiConnectionErrors.Describe(ex, Provider); ConsoleLogger.Warn("[AI_SETUP] " + Status); }
        finally { End(); }
    }

    public async Task MatchAsync()
    {
        if (!IsIdle) return;
        // Rules and approved aliases need no key and send nothing anywhere. Without a tested key
        // and permission the match still runs that far, and leaves the rest for review.
        if (!IsReady || !AllowExternalMatching) { await MatchWithRulesOnlyAsync(); return; }
        var group = SelectedGroup;
        var names = ObservedNames.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (group == null || group.Students.Count == 0 || names.Length == 0) { Status = "Select a populated group and enter observed names, one per line."; return; }
        if (names.Length > 500 || names.Any(n => n.Length > 200)) { Status = "Use at most 500 names, each no longer than 200 characters."; return; }
        Begin(); ClearResults();
        try
        {
            var settings = _store.Read() ?? throw new InvalidOperationException();
            if (settings.Provider != Provider || settings.Model != Model.Trim()) { IsReady = false; Status = "Saved provider/model changed. Test the connection again."; return; }
            Status = "Matching → rules → approved aliases → AI for uncertain names…";
            var result = await _service.MatchAsync(settings, group, names, _operation!.Token);
            _operation.Token.ThrowIfCancellationRequested();
            if (!Equals(SelectedGroup, group)) { Status = "Group changed; stale matching results discarded."; return; }
            foreach (var student in result.Students) Results.Add(student);
            foreach (var item in result.ReviewQueue) Review.Add(item);
            OnPropertyChanged(nameof(ConfidenceSummary));
            Status = result.Diagnostics.Count > 0
                ? $"Completed with warnings — {Review.Count} review items. No absence calculated. " + string.Join(" ", result.Diagnostics.Distinct().Take(4))
                : $"Done — {Results.Count(r => r.Status == AttendanceMatchStatus.Present)} present; {Review.Count} review items. No absence calculated.";
        }
        catch (Exception ex) { Status = AiConnectionErrors.Describe(ex, Provider); }
        finally { End(); }
    }

    private void RemoveKey()
    {
        if (!IsIdle) return;
        try
        {
            if (_store.Read() is { } saved && saved.Provider != Provider)
            { Status = $"Saved key belongs to {saved.Provider}. Select that provider to remove it."; return; }
            _store.Delete(); HasSavedKey = false; IsReady = false; Status = "Done — saved AI key removed.";
        }
        catch { Status = "Failed to remove the saved key from Windows Credential Manager."; }
    }
    /// <summary>Names against the roster locally, for when no AI is set up or permitted.</summary>
    private async Task MatchWithRulesOnlyAsync()
    {
        var group = SelectedGroup;
        var names = ObservedNames.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (group == null || group.Students.Count == 0 || names.Length == 0) { Status = "Select a populated group and enter observed names, one per line."; return; }
        Begin(); ClearResults();
        try
        {
            var result = await _service.MatchWithRulesOnlyAsync(group, names, _operation!.Token);
            _operation.Token.ThrowIfCancellationRequested();
            if (!Equals(SelectedGroup, group)) { Status = "Group changed; stale matching results discarded."; return; }
            foreach (var student in result.Students) Results.Add(student);
            foreach (var item in result.ReviewQueue) Review.Add(item);
            OnPropertyChanged(nameof(ConfidenceSummary));
            string why = IsReady ? "AI matching is not permitted" : "no AI key is set up";
            Status = $"Done by name rules only — {Results.Count(r => r.Status == AttendanceMatchStatus.Present)} present; "
                + $"{Review.Count} review item{(Review.Count == 1 ? "" : "s")}. Uncertain names were left for review because {why}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = ex.Message; }
        finally { End(); }
    }

    private void Begin() { IsIdle = false; _operation = new CancellationTokenSource(TimeSpan.FromMinutes(3)); }
    private void End() { _operation?.Dispose(); _operation = null; IsIdle = true; }
    public static string SafeFailure(Exception ex) => AiConnectionErrors.Describe(ex);
    public void Dispose() => _operation?.Cancel();
}
