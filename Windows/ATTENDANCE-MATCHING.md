# Standalone attendance name matching

`ZoomAutoAdmit.AttendanceMatching` targets .NET 8 and references only the Roster module. It has **no dependency or subscription** to Auto Admit, WaitingRoomTester, account switching, scheduling, meeting lifecycle, or Attendance Collector. Nothing runs automatically. Existing roster records and raw presence snapshots are read-only inputs; the only new persisted data is alias memory.

## Explicit batch API

```csharp
using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Roster;

RosterGroup selectedGroup = /* obtain the selected group from IGroupRosterService */;
IReadOnlyList<string> observedZoomNames = /* raw observed participant names */;
var matcher = new AttendanceMatchingEngine(new JsonAliasMemory());
AttendanceMatchResult result = await matcher.MatchAsync(selectedGroup, observedZoomNames, cancellationToken);
```

The default constructor setup above is offline. Uncertain observations are queued for review, not sent externally. The returned student rows preserve ascending **numeric roster `Order`**, including gaps; names are never alphabetically sorted. Input collections are not modified.

Each row has StudentId, Order, StudentName, Status, Confidence, MatchSource, and confirmed ObservedNames. Status is serialized as `Present`, `NeedsReview`, or `NotObserved`; source as `Rule`, `Alias`, `AI`, or `None`. There is deliberately no Absent status. Unresolved observations include candidate IDs, scores, sources, and reasons in `ReviewQueue`. An unknown name may implicate all students, while an invalid/empty observation has no candidates. Independent confirmed presence is not downgraded by another ambiguous observation.

## Decision policy

1. Normalize Unicode/case, whitespace/punctuation, Latin diacritics, Arabic diacritics/tatweel and alef/ya variants. A small conservative vocabulary recognizes common Arabic/English spellings. Arabizi digits inside Latin tokens have approximate equivalents (e.g. `Mo7ab` -> `mohab`). This is not exhaustive transliteration.
2. Compare full names, complete observed first-two/three-name prefixes, first/second-name evidence, and first+family names. Full normalized match scores 100; complete prefix scores 95 or 98. First+family with omitted middle names scores 88 and requires AI/review. Contradictory extra tokens do not receive prefix confidence.
3. Check explicit roster aliases and approved memory before AI. All candidates compete within the **selected group**, including alias/name collisions. Unique high confidence (default >=95), a >=10-point separation from alternatives, and at least two distinct name tokens are required. First-only, family-only, or duplicate full-name/prefix evidence never suffices; no index-based tie-breaks.
4. For uncertain evidence, compare each plausible candidate using `IAiNameMatcher`. If no lexical candidate exists, consider the entire group. Default limits: 8 candidates per observation and 100 calls per batch. Exceeding either sends the observation to review without silently pruning alternatives. Repeated normalized observations are processed once per batch.
5. Confirm AI only when exactly one candidate returns `match=true`, `needsReview=false`, confidence >=95, with the expected student ID; all other compared candidates must reject without review. Failures, unresolved competitors, single-token evidence, and family-only evidence cannot be auto-confirmed even by an overconfident model.

Confidence values are heuristic/model scores, **not calibrated probabilities or proof of identity**. Ambiguous transliterations and similarly named students need human review. This module neither implements review UI/approval workflows nor computes attendance duration, absences, grades, or reports.

## Optional real external AI connector

`OpenAiNameMatcher` implements the OpenAI Responses API with a strict JSON schema, following [official Structured Outputs documentation](https://developers.openai.com/api/docs/guides/structured-outputs). It is replaceable through `IAiNameMatcher` without changing the matching engine.

The caller must explicitly configure a model supporting Responses structured output and supply `OPENAI_API_KEY` through the environment or an injected secret-provider delegate. No model/key is silently selected, loaded from account credentials, or written to disk.

```csharp
// Caller owns/disposes HttpClient. Disable redirects to keep names on the intended endpoint.
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
string model = Environment.GetEnvironmentVariable("ATTENDANCE_AI_MODEL")
    ?? throw new InvalidOperationException("Set ATTENDANCE_AI_MODEL explicitly.");
var ai = new OpenAiNameMatcher(http, model); // OPENAI_API_KEY read only at request time
var matcher = new AttendanceMatchingEngine(new JsonAliasMemory(), ai,
    new MatchingOptions { ConfidenceThreshold = 95, AiTimeout = TimeSpan.FromSeconds(20) });
var result = await matcher.MatchAsync(selectedGroup, observedZoomNames, cancellationToken);
```

External calls send only a single candidate's studentId/fullName and the observed name, not email, the entire roster, meeting URLs, or credentials. Names are separated as untrusted JSON data from system instructions. Response storage is disabled via `store=false`; this is **not** a promise of zero provider retention. Configure/use the provider only when permitted to send student names externally. No real student data is sent by automated tests.

The model answer is JSON only:

```json
{"match":true,"confidence":96,"studentId":"S1","reason":"First and family names agree","needsReview":false}
```

The connector validates the schema, expected ID, ranges, and reason. Extra/missing/duplicate fields, refusal, incomplete output, invalid JSON, HTTP failures, or timeout result in review, never absence. Responses are capped at 64 KB. The engine supplies a per-call deadline and cancellation. No automatic paid-request retry loop is used. Callers of the connector directly should supply their own deadline. Caller cancellation propagates; it is not swallowed as uncertainty.

## Alias memory

File: `%LOCALAPPDATA%\ZoomAutoAdmit\Attendance\aliases.json` (created on first accepted AI alias).

Only unambiguous accepted AI matches are learned automatically. `approved=true` means **accepted by the configured matching policy**, not a claim that a person reviewed it. Entries contain studentId, raw alias, confidence, approved, groupId, normalized roster-name fingerprint, and UTC createdAt. Aliases are reused only for the same group/student and unchanged normalized roster name, with sufficient confidence. Deleted student IDs, different groups, stale names, and unapproved/weak aliases are ignored.

Persistence uses a cross-process exclusive lock, atomic replacement, one previous-version `.bak`, validation and an 8 MB safety limit. Conflicting normalized alias assignments within a group are rejected. Corrupt memory is preserved, disabled for that batch, and reported in Diagnostics; rule matching can continue. Failure to persist an accepted alias is reported without rewriting raw attendance or claiming it was saved. Files inherit local directory permissions; this module adds no encryption or synchronization.

## Files and verification

- `MatchingModels.cs`: contracts, results/review queue, policy options.
- `NameNormalizer.cs` / `RuleBasedNameMatcher.cs`: deterministic evidence.
- `AttendanceMatchingEngine.cs`: ordered batch decisions and guarded AI fallback.
- `OpenAiNameMatcher.cs`: isolated external HTTP adapter.
- `JsonAliasMemory.cs`: independent safe storage.
- `tests/ZoomAutoAdmit.AttendanceMatching.Tests`: synthetic names, temporary storage, fake AI and mocked HTTP; no Zoom lifecycle is started.

```powershell
dotnet test Windows/tests/ZoomAutoAdmit.AttendanceMatching.Tests
dotnet build Windows/ZoomAutoAdmit.Windows.sln
dotnet test Windows/ZoomAutoAdmit.Windows.sln --no-build -m:1
```
