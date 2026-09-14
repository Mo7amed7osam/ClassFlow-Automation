import Foundation

/// The minimal payload sent for AI matching.
///
/// Only unresolved names travel: no roster that already matched, no other
/// group's students, no schedule, no account, no logs. Students are referenced
/// by an opaque per-request identifier so the model never needs anything else
/// about them.
public struct AIMatchRequest: Equatable {
    public struct Candidate: Equatable {
        public let id: String
        public let officialName: String
        /// Already matched on another Zoom name: only a second identity of the same person may
        /// be paired with them.
        public let alreadyPresent: Bool

        public init(id: String, officialName: String, alreadyPresent: Bool = false) {
            self.id = id
            self.officialName = officialName
            self.alreadyPresent = alreadyPresent
        }
    }

    public struct ObservedName: Equatable {
        public let id: String
        public let displayName: String

        public init(id: String, displayName: String) {
            self.id = id
            self.displayName = displayName
        }
    }

    public let students: [Candidate]
    public let observedNames: [ObservedName]

    public init(students: [Candidate], observedNames: [ObservedName]) {
        self.students = students
        self.observedNames = observedNames
    }

    public var isWorthSending: Bool { !students.isEmpty && !observedNames.isEmpty }
}

/// Opaque request IDs mapped back to the current session only. Neither UUID map
/// leaves the process, and validation accepts no model-returned ID outside it.
public struct AIMatchRequestIDs: Equatable {
    public let students: [String: UUID]
    public let observations: [String: UUID]

    public init(students: [String: UUID], observations: [String: UUID]) {
        self.students = students
        self.observations = observations
    }
}

/// What the model is allowed to say back.
public struct AIMatchProposal: Codable, Equatable {
    public let studentID: String
    public let observedNameID: String
    public let confidence: Double
    public let needsReview: Bool?
    public let reason: String?

    public init(
        studentID: String,
        observedNameID: String,
        confidence: Double,
        needsReview: Bool? = nil,
        reason: String? = nil
    ) {
        self.studentID = studentID
        self.observedNameID = observedNameID
        self.confidence = confidence
        self.needsReview = needsReview
        self.reason = reason
    }

    private enum CodingKeys: String, CodingKey {
        case studentID = "student_id"
        case observedNameID = "observed_name_id"
        case confidence
        case needsReview = "needs_review"
        case reason
    }
}

public struct AIMatchResponse: Codable, Equatable {
    public let matches: [AIMatchProposal]
    public let unmatchedStudentIDs: [String]?
    public let unmatchedObservedNameIDs: [String]?

    public init(
        matches: [AIMatchProposal],
        unmatchedStudentIDs: [String]? = nil,
        unmatchedObservedNameIDs: [String]? = nil
    ) {
        self.matches = matches
        self.unmatchedStudentIDs = unmatchedStudentIDs
        self.unmatchedObservedNameIDs = unmatchedObservedNameIDs
    }

    private enum CodingKeys: String, CodingKey {
        case matches
        case unmatchedStudentIDs = "unmatched_student_ids"
        case unmatchedObservedNameIDs = "unmatched_observed_name_ids"
    }
}

public enum AIMatchError: Error, Equatable {
    case noAPIKey
    case notConfigured
    case network(String)
    case httpStatus(Int)
    case malformedResponse(String)

    public var message: String {
        switch self {
        case .noAPIKey: return "No OpenRouter API key is set."
        case .notConfigured: return "AI matching is turned off."
        case .network(let detail): return "OpenRouter could not be reached: \(detail)"
        case .httpStatus(let code): return "OpenRouter returned HTTP \(code)."
        case .malformedResponse(let detail): return "OpenRouter's reply could not be used: \(detail)"
        }
    }
}

/// Validates model output before it is allowed anywhere near the register.
///
/// Everything the model returns is untrusted. It may name a student who does
/// not exist, a Zoom name nobody used, the same person twice, or a confidence
/// outside any sane range. Each of those is discarded here rather than being
/// discovered later as a wrong attendance mark.
public enum AIMatchValidator {
    public struct ValidatedMatch: Equatable {
        public let studentID: UUID
        public let observationID: UUID
        public let zoomName: String
        public let confidence: Double
        public let needsReview: Bool
        public let reason: String?
    }

    public struct Result: Equatable {
        public let matches: [ValidatedMatch]
        /// Proposals thrown away, with the reason, for the log.
        public let rejected: [String]
    }

    public static func validate(
        _ response: AIMatchResponse,
        students: [Student],
        observations: [ParticipantObservation],
        requestIDs: AIMatchRequestIDs
    ) -> Result {
        var matches: [ValidatedMatch] = []
        var rejected: [String] = []
        var usedStudents = Set<UUID>()
        var usedObservations = Set<UUID>()

        for proposal in response.matches {
            guard let studentID = requestIDs.students[proposal.studentID],
                  students.contains(where: { $0.id == studentID }) else {
                rejected.append("unknown student id \(proposal.studentID)")
                continue
            }

            // The opaque ID must map to an observation that was both genuinely
            // seen and explicitly included in this request. Looking up arbitrary
            // returned name text in the whole session would let the model claim
            // an observation that local matching had already consumed.
            guard let observationID = requestIDs.observations[proposal.observedNameID],
                  let observation = observations.first(where: { $0.id == observationID }) else {
                rejected.append("unknown observed name id \(proposal.observedNameID)")
                continue
            }

            guard proposal.confidence.isFinite, proposal.confidence >= 0, proposal.confidence <= 1 else {
                rejected.append("confidence out of range for \(proposal.observedNameID)")
                continue
            }

            // One student may take several observed names (the same person joining twice); one
            // observed name may never go to two students.
            guard !usedObservations.contains(observation.id) else {
                rejected.append("duplicate observed name assignment: \(proposal.observedNameID)")
                continue
            }

            usedStudents.insert(studentID)
            usedObservations.insert(observation.id)
            matches.append(ValidatedMatch(
                studentID: studentID,
                observationID: observation.id,
                zoomName: observation.rawName,
                confidence: proposal.confidence,
                needsReview: proposal.needsReview ?? false,
                reason: proposal.reason
            ))
        }

        for studentID in response.unmatchedStudentIDs ?? []
            where requestIDs.students[studentID] == nil {
            rejected.append("unknown unmatched student id \(studentID)")
        }
        for observedNameID in response.unmatchedObservedNameIDs ?? []
            where requestIDs.observations[observedNameID] == nil {
            rejected.append("unknown unmatched observed name id \(observedNameID)")
        }

        return Result(matches: matches, rejected: rejected)
    }

    /// Pulls the JSON object out of a reply that may be wrapped in prose or a
    /// fenced code block, then decodes it strictly.
    public static func decode(_ text: String) throws -> AIMatchResponse {
        let cleaned = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let candidate: String

        if let start = cleaned.firstIndex(of: "{"), let end = cleaned.lastIndex(of: "}"), start < end {
            candidate = String(cleaned[start...end])
        } else {
            throw AIMatchError.malformedResponse("no JSON object in the reply")
        }

        do {
            return try JSONDecoder().decode(AIMatchResponse.self, from: Data(candidate.utf8))
        } catch {
            throw AIMatchError.malformedResponse("\(error)")
        }
    }
}
