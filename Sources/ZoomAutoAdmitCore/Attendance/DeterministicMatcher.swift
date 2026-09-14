import Foundation

/// A single student-to-observation pairing under consideration.
public struct MatchCandidate: Equatable {
    public let studentID: UUID
    public let observationID: UUID
    public let score: Double
    public let source: MatchSource
    public let reason: String

    public init(studentID: UUID, observationID: UUID, score: Double, source: MatchSource, reason: String) {
        self.studentID = studentID
        self.observationID = observationID
        self.score = score
        self.source = source
        self.reason = reason
    }
}

public struct MatchingOutcome: Equatable {
    /// Confident pairings, one student to one observation.
    public var accepted: [MatchCandidate]
    /// Plausible but not confident enough to record without a human.
    public var review: [MatchCandidate]
    /// A second Zoom identity of a student already accepted: the same person joined twice
    /// (phone and laptop, or rejoined under another name). Evidence for that student, never
    /// an extra attendee and never an unmatched stranger.
    public var duplicates: [MatchCandidate]
    public var unmatchedStudentIDs: [UUID]
    public var unmatchedObservationIDs: [UUID]

    public init(
        accepted: [MatchCandidate] = [],
        review: [MatchCandidate] = [],
        duplicates: [MatchCandidate] = [],
        unmatchedStudentIDs: [UUID] = [],
        unmatchedObservationIDs: [UUID] = []
    ) {
        self.accepted = accepted
        self.review = review
        self.duplicates = duplicates
        self.unmatchedStudentIDs = unmatchedStudentIDs
        self.unmatchedObservationIDs = unmatchedObservationIDs
    }
}

/// Local matching, run before any AI is considered.
///
/// Ordering is exact, then learned alias, then token overlap, then fuzzy
/// similarity. Most of a class resolves here, which keeps the AI layer for the
/// genuinely ambiguous handful — cheaper, faster, less data leaving the machine,
/// and far less room for a confident-sounding wrong answer.
public enum DeterministicMatcher {
    /// Below this, a pairing is not even worth showing to a human.
    public static let reviewFloor = 0.55
    /// Device-style names never auto-accept, however well they score.
    public static let deviceNameCeiling = 0.5
    /// Pairings whose first names disagree stay below automatic acceptance.
    public static let differentFirstNameCeiling = 0.75

    static func isHumanConfirmed(_ pairing: ScoredPairing) -> Bool {
        pairing.source == .exact || pairing.source == .alias
    }

    /// The same given name, allowing a spelling slip ("ahmed"/"ahmad", "mohamed"/"mohammed").
    static func firstNamesAgree(_ observed: String, _ official: String) -> Bool {
        observed == official
            || NameSimilarity.jaroWinkler(observed, official) >= 0.9
            || (min(observed.count, official.count) >= 3 && (observed.hasPrefix(official) || official.hasPrefix(observed)))
    }

    /// A second name for a student who is already present must score at least this to be
    /// folded into them. Higher than the review floor: a loose resemblance to a present
    /// student must not swallow a stranger the operator should see.
    public static let duplicateFloor = 0.8

    /// Titles people put before their name in Zoom. They say nothing about who someone is.
    static let honorifics: Set<String> = ["dr", "doctor", "dcotr", "dctor", "doc", "eng", "engineer", "mr", "mrs", "ms", "miss", "prof", "professor", "د", "دكتور", "دكتوره", "م", "مهندس", "مهندسه", "ا", "استاذ", "استاذه"]

    /// One pairing's score, with why.
    public struct ScoredPairing: Equatable {
        public var score: Double
        public var source: MatchSource
        public var reason: String

        public init(score: Double, source: MatchSource, reason: String) {
            self.score = score
            self.source = source
            self.reason = reason
        }
    }

    /// Scores one Zoom name against one student.
    ///
    /// Public so the review UI can offer the same suggestion the matcher would
    /// have made. A reviewer picking from a long unsorted list is being asked to
    /// redo work the matcher already did; ranking by this keeps the two in step,
    /// and there is only ever one scoring rule to reason about.
    public static func score(rawObservedName: String, student: Student) -> ScoredPairing? {
        scoreIgnoringTitles(
            rawObservedName: rawObservedName,
            normalizedObservedName: NameNormalizer.normalize(rawObservedName),
            officialNormalized: student.normalizedOfficialName,
            officialTokens: NameNormalizer.tokens(student.officialName),
            aliasSet: Set(student.aliases.map(NameNormalizer.normalize))
        )
    }

    /// Scores the name as written and without a leading title ("Dr", "Eng", "م"), keeping the better.
    private static func scoreIgnoringTitles(
        rawObservedName: String,
        normalizedObservedName observed: String,
        officialNormalized: String,
        officialTokens: [String],
        aliasSet: Set<String>
    ) -> ScoredPairing? {
        let asWritten = score(rawObservedName: rawObservedName, normalizedObservedName: observed, officialNormalized: officialNormalized, officialTokens: officialTokens, aliasSet: aliasSet)
        var words = observed.split(separator: " ").map(String.init)
        var stripped = false
        while words.count > 1, let first = words.first, honorifics.contains(first) {
            words.removeFirst()
            stripped = true
        }
        guard stripped else { return asWritten }
        let untitled = words.joined(separator: " ")
        guard var withoutTitle = score(rawObservedName: untitled, normalizedObservedName: untitled, officialNormalized: officialNormalized, officialTokens: officialTokens, aliasSet: aliasSet) else { return asWritten }
        guard withoutTitle.score > (asWritten?.score ?? 0) else { return asWritten }
        withoutTitle.reason += "; title ignored"
        return withoutTitle
    }

    private static func score(
        rawObservedName: String,
        normalizedObservedName observed: String,
        officialNormalized: String,
        officialTokens: [String],
        aliasSet: Set<String>
    ) -> ScoredPairing? {
        guard !observed.isEmpty else { return nil }

        var candidate: ScoredPairing

        if observed == officialNormalized {
            candidate = ScoredPairing(score: 1.0, source: .exact, reason: "Name matches the roster exactly")
        } else if aliasSet.contains(observed) {
            candidate = ScoredPairing(score: 1.0, source: .alias, reason: "Known alias for this student")
        } else {
            let observedTokens = NameNormalizer.tokens(rawObservedName)
            let token = NameSimilarity.tokenSimilarity(
                observed: observedTokens,
                official: officialTokens
            )
            let whole = max(
                NameSimilarity.jaroWinkler(observed, officialNormalized),
                NameSimilarity.levenshteinSimilarity(observed, officialNormalized)
            )
            if token >= whole {
                candidate = ScoredPairing(
                    score: token,
                    source: .token,
                    reason: tokenReason(observedTokens, officialTokens)
                )
            } else {
                candidate = ScoredPairing(score: whole, source: .fuzzy, reason: "Names are similar")
            }
        }

        // Sharing name parts is not enough on its own when the first names differ: "Ibrahim
        // Mohamed" shares two parts with "Seham Mohamed Helmy Ibrahem Rezk" and is somebody
        // else entirely. Zoom names start with the given name, so without it this is a
        // question for review, never an automatic Present.
        if !isHumanConfirmed(candidate),
           let observedFirst = NameNormalizer.tokens(rawObservedName).first,
           let officialFirst = officialTokens.first,
           !firstNamesAgree(observedFirst, officialFirst) {
            candidate.score = min(candidate.score, differentFirstNameCeiling)
            candidate.reason += "; first names differ"
        }

        // A device name may well be a student, but it is not evidence —
        // unless a human already told us whose device it is. An exact
        // roster name or a learned alias outranks the heuristic.
        let isHumanConfirmed = candidate.source == .exact || candidate.source == .alias
        if !isHumanConfirmed,
           NameNormalizer.looksLikeDeviceName(rawObservedName)
            || NameNormalizer.isLowSignal(rawObservedName) {
            candidate.score = min(candidate.score, deviceNameCeiling)
            candidate.reason += "; Zoom name looks like a device"
        }

        return candidate
    }

    public static func match(
        students: [Student],
        observations: [ParticipantObservation],
        autoAcceptConfidence: Double
    ) -> MatchingOutcome {
        var candidates: [MatchCandidate] = []

        for student in students {
            let officialTokens = NameNormalizer.tokens(student.officialName)
            let officialNormalized = NameNormalizer.normalize(student.officialName)
            let aliasSet = Set(student.aliases.map(NameNormalizer.normalize))

            for observation in observations {
                guard let candidate = scoreIgnoringTitles(
                    rawObservedName: observation.rawName,
                    normalizedObservedName: observation.normalizedName,
                    officialNormalized: officialNormalized,
                    officialTokens: officialTokens,
                    aliasSet: aliasSet
                ) else { continue }

                guard candidate.score >= reviewFloor else { continue }
                candidates.append(MatchCandidate(
                    studentID: student.id,
                    observationID: observation.id,
                    score: candidate.score,
                    source: candidate.source,
                    reason: candidate.reason
                ))
            }
        }

        return assign(
            candidates: candidates,
            students: students,
            observations: observations,
            autoAcceptConfidence: autoAcceptConfidence
        )
    }

    /// Greedy one-to-one assignment, strongest pairing first.
    ///
    /// Independent yes/no scoring would happily mark two students present from
    /// one Zoom name, or claim one student joined twice under different names.
    /// Taking the strongest pairing first and then removing both sides keeps the
    /// register honest.
    private static func assign(
        candidates: [MatchCandidate],
        students: [Student],
        observations: [ParticipantObservation],
        autoAcceptConfidence: Double
    ) -> MatchingOutcome {
        let ordered = candidates.sorted {
            if $0.score != $1.score { return $0.score > $1.score }
            return $0.studentID.uuidString < $1.studentID.uuidString
        }

        var usedStudents = Set<UUID>()
        var usedObservations = Set<UUID>()
        var accepted: [MatchCandidate] = []
        var review: [MatchCandidate] = []

        var duplicates: [MatchCandidate] = []
        var acceptedStudents = Set<UUID>()

        for candidate in ordered {
            guard !usedObservations.contains(candidate.observationID) else { continue }
            if usedStudents.contains(candidate.studentID) {
                // One person, two Zoom identities: a strong pairing with a student already
                // accepted as present is that student joining again - unless the identity fits
                // a student not yet placed about as well, which is left for them.
                let contested = ordered.contains {
                    $0.observationID == candidate.observationID && !usedStudents.contains($0.studentID)
                        && $0.score >= candidate.score - 0.05
                }
                if candidate.score >= duplicateFloor, acceptedStudents.contains(candidate.studentID), !contested {
                    duplicates.append(candidate)
                    usedObservations.insert(candidate.observationID)
                }
                continue
            }
            if candidate.score >= autoAcceptConfidence {
                accepted.append(candidate)
                acceptedStudents.insert(candidate.studentID)
            } else {
                review.append(candidate)
            }
            usedStudents.insert(candidate.studentID)
            usedObservations.insert(candidate.observationID)
        }

        return MatchingOutcome(
            accepted: accepted,
            review: review,
            duplicates: duplicates,
            unmatchedStudentIDs: students.map(\.id).filter { !usedStudents.contains($0) },
            unmatchedObservationIDs: observations.map(\.id).filter { !usedObservations.contains($0) }
        )
    }

    private static func tokenReason(_ observed: [String], _ official: [String]) -> String {
        let shared = Set(observed).intersection(official)
        if shared.isEmpty { return "Name parts are similar" }
        if observed.count < official.count {
            return "Shares \(shared.count) name part(s); middle name omitted"
        }
        return "Shares \(shared.count) name part(s)"
    }
}
