using ZoomAutoAdmit.Roster;

namespace ZoomAutoAdmit.AttendanceMatching;

/// <summary>Explicit batch operation only. Does not subscribe to or mutate meeting/collector state.</summary>
public sealed class AttendanceMatchingEngine
{
    private readonly IAliasMemory _memory;
    private readonly IAiNameMatcher? _ai;
    private readonly MatchingOptions _options;
    private readonly Action<string> _log;
    /// <summary>Every AI answer given before: the same comparison is never sent again.</summary>
    private readonly IAiDecisionMemory? _decisions;
    private sealed record Candidate(GroupStudent Student, int Confidence, MatchSource Source, string Reason);

    public AttendanceMatchingEngine(IAliasMemory memory, IAiNameMatcher? ai = null,
        MatchingOptions? options = null, Action<string>? log = null, IAiDecisionMemory? decisions = null)
    {
        _decisions = decisions;
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _ai = ai;
        _options = options ?? new();
        _options.Validate();
        _log = log ?? Console.WriteLine;
    }

    public async Task<AttendanceMatchResult> MatchAsync(RosterGroup roster, IReadOnlyList<string> observedNames,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateRoster(roster);
        ArgumentNullException.ThrowIfNull(observedNames);
        // Freeze caller-owned collections before awaiting external I/O.
        var students = roster.Students.OrderBy(s => s.Order).Select(s => s with { Aliases = s.Aliases.ToArray() }).ToArray();
        var observations = observedNames.ToArray();
        var results = students.ToDictionary(s => s.StudentId,
            s => new StudentMatchResult(s.StudentId, s.Order, s.FullName, AttendanceMatchStatus.NotObserved, 0, MatchSource.None, []));
        var review = new List<AttendanceReviewItem>();
        var diagnostics = new List<string>();
        var aliases = new List<ApprovedAlias>();
        bool memoryAvailable = true;
        try { aliases.AddRange(await _memory.LoadAsync(token)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            memoryAvailable = false;
            Diagnostic("Alias memory unavailable; not overwritten", ex);
        }
        int calls = 0, remembered = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observed in observations)
        {
            token.ThrowIfCancellationRequested();
            string name;
            try { name = NameNormalizer.Normalize(observed); }
            catch (ArgumentException) { Queue(observed, "Invalid or oversized observed name.", []); continue; }
            if (name.Length == 0) { Queue(observed, "Empty/non-name observation.", []); continue; }
            if (!seen.Add(name)) continue;
            var candidates = students.Select(s => Score(s, observed, name, aliases)).OrderByDescending(c => c.Confidence).ToArray();
            var high = candidates.Where(c => c.Confidence >= _options.ConfidenceThreshold).ToArray();
            if (high.Length == 1 && NameNormalizer.HasMultipleNames(observed) &&
                (candidates.Length == 1 || high[0].Confidence - candidates[1].Confidence >= _options.AmbiguityMargin))
            {
                Confirm(high[0], observed);
                continue;
            }
            // Equal or near-equal deterministic evidence cannot safely be broken by a pairwise AI guess.
            if (high.Length > 0)
            {
                Queue(observed, "Ambiguous or insufficient identity evidence; no automatic tie-break.",
                    candidates.Where(c => c.Confidence > 0));
                continue;
            }
            var possible = candidates.Where(c => c.Confidence > 0).ToArray();
            if (possible.Length == 0) possible = candidates;
            if (_ai == null || possible.Length == 0)
            {
                Queue(observed, _ai == null ? "AI is not configured; manual review required." : "Roster is empty.", possible);
                continue;
            }
            // Comparisons answered before cost nothing; only the rest count against the budget.
            int toAsk = possible.Count(c => _decisions == null ||
                !_decisions.TryGet(roster.GroupId, c.Student.StudentId, c.Student.FullName, observed, out _));
            if (possible.Length > _options.MaximumAiCandidates || toAsk > _options.MaximumAiCalls - calls)
            {
                Queue(observed, "AI candidate/request budget reached; candidates were not silently truncated.", possible);
                continue;
            }
            var answers = new List<(Candidate Candidate, AiNameMatch Answer)>();
            bool failed = false;
            foreach (var candidate in possible)
            {
                token.ThrowIfCancellationRequested();
                if (_decisions != null &&
                    _decisions.TryGet(roster.GroupId, candidate.Student.StudentId, candidate.Student.FullName, observed, out var known))
                {
                    answers.Add((candidate, known));
                    remembered++;
                    continue;
                }
                calls++;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(_options.AiTimeout);
                try
                {
                    var answer = await _ai.MatchAsync(candidate.Student, observed, timeout.Token).WaitAsync(timeout.Token);
                    OpenAiNameMatcher.ValidateResult(answer, candidate.Student.StudentId);
                    answers.Add((candidate, answer));
                    _decisions?.Save(roster.GroupId, candidate.Student.StudentId, candidate.Student.FullName, observed, answer);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { failed = true; Diagnostic("AI comparison unavailable or invalid", ex); }
            }
            var confirmed = answers.Where(a => a.Answer.Match && !a.Answer.NeedsReview &&
                a.Answer.Confidence >= _options.ConfidenceThreshold).ToArray();
            if (!failed && confirmed.Length == 1 && NameNormalizer.HasMultipleNames(observed) &&
                !confirmed[0].Candidate.Reason.StartsWith("Family name only", StringComparison.Ordinal) &&
                answers.Where(a => a.Candidate.Student.StudentId != confirmed[0].Candidate.Student.StudentId)
                    .All(a => !a.Answer.Match && !a.Answer.NeedsReview))
            {
                var winner = confirmed[0];
                var match = winner.Candidate with { Confidence = winner.Answer.Confidence, Source = MatchSource.AI, Reason = winner.Answer.Reason };
                Confirm(match, observed);
                if (memoryAvailable)
                {
                    var alias = new ApprovedAlias(winner.Candidate.Student.StudentId, observed, winner.Answer.Confidence,
                        true, roster.GroupId, NameNormalizer.Normalize(winner.Candidate.Student.FullName), DateTimeOffset.UtcNow);
                    try
                    {
                        await _memory.SaveAsync(alias, token);
                        aliases.RemoveAll(a => a.GroupId == alias.GroupId && NameNormalizer.Normalize(a.Alias) == name);
                        aliases.Add(alias);
                        Log("Alias saved");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Diagnostic("Match confirmed but alias was not saved", ex); }
                }
            }
            else
            {
                Queue(observed, failed ? "AI failed or timed out; manual review required." :
                    "AI uncertain, conflicting, or insufficient identity evidence.", possible.Select(c =>
                {
                    var answer = answers.FirstOrDefault(a => a.Candidate.Student.StudentId == c.Student.StudentId).Answer;
                    return answer == null ? c : c with { Confidence = answer.Confidence, Source = MatchSource.AI, Reason = answer.Reason };
                }));
            }
        }
        Log($"Matching completed; Present: {results.Values.Count(r => r.Status == AttendanceMatchStatus.Present)}; Review: {review.Count}" +
            (_ai != null ? $"; AI requests: {calls} (answered from memory: {remembered})" : ""));
        return new(roster.GroupId, students.Select(s => results[s.StudentId]).ToArray(), review.AsReadOnly(), diagnostics.AsReadOnly());

        void Confirm(Candidate candidate, string observed)
        {
            var current = results[candidate.Student.StudentId];
            bool replace = current.Status != AttendanceMatchStatus.Present || candidate.Confidence > current.Confidence;
            results[candidate.Student.StudentId] = current with
            {
                Status = AttendanceMatchStatus.Present,
                Confidence = replace ? candidate.Confidence : current.Confidence,
                MatchSource = replace ? candidate.Source : current.MatchSource,
                ObservedNames = current.ObservedNames.Append(observed).Distinct(StringComparer.Ordinal).ToArray()
            };
            Log($"Match confirmed; Source: {candidate.Source}; Confidence: {candidate.Confidence}");
        }

        void Queue(string? observed, string reason, IEnumerable<Candidate> candidates)
        {
            var choices = candidates.ToArray();
            review.Add(new(observed ?? "", reason, choices.Select(c => new ReviewCandidate(
                c.Student.StudentId, c.Confidence, c.Source, c.Reason)).ToArray()));
            foreach (var candidate in choices)
            {
                var current = results[candidate.Student.StudentId];
                if (current.Status == AttendanceMatchStatus.Present) continue;
                results[candidate.Student.StudentId] = current with
                {
                    Status = AttendanceMatchStatus.NeedsReview,
                    Confidence = Math.Max(current.Confidence, candidate.Confidence),
                    MatchSource = candidate.Confidence >= current.Confidence ? candidate.Source : current.MatchSource
                };
            }
            Log("Needs Review: " + reason);
        }

        void Diagnostic(string message, Exception ex)
        {
            // Do not log remote response bodies, API keys, or personal names in exception messages.
            string safe = message + " (" + ex.GetType().Name + ").";
            diagnostics.Add(safe);
            Log(safe);
        }
    }

    private Candidate Score(GroupStudent student, string observed, string normalized, IReadOnlyList<ApprovedAlias> aliases)
    {
        var rule = RuleBasedNameMatcher.Evaluate(student, observed);
        var best = new Candidate(student, rule.Confidence, MatchSource.Rule, rule.Reason);
        if (student.Aliases.Any(a => NameNormalizer.Normalize(a) == normalized))
            best = new(student, 100, MatchSource.Alias, "Explicit roster alias.");
        foreach (var alias in aliases.Where(a => a.Approved && a.GroupId == student.GroupId &&
                     a.StudentId == student.StudentId && a.Confidence >= _options.ConfidenceThreshold && a.Confidence <= 100 &&
                     a.RosterName == NameNormalizer.Normalize(student.FullName) && NameNormalizer.Normalize(a.Alias) == normalized))
            if (alias.Confidence >= best.Confidence) best = new(student, alias.Confidence, MatchSource.Alias, "Approved AI alias memory.");
        return best;
    }

    private static void ValidateRoster(RosterGroup roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentException.ThrowIfNullOrWhiteSpace(roster.GroupId);
        if (roster.Students == null) throw new ArgumentException("Students are required.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orders = new HashSet<int>();
        foreach (var student in roster.Students)
        {
            if (student == null || student.GroupId != roster.GroupId || student.Order <= 0 ||
                !ids.Add(student.StudentId) || !orders.Add(student.Order)) throw new ArgumentException("Invalid roster identity/order.");
            StudentValidation.Normalize(new(student.StudentId, student.FullName, student.Aliases, student.Email));
        }
    }

    private void Log(string message) { try { _log("[MATCHING] " + message); } catch { } }
}
