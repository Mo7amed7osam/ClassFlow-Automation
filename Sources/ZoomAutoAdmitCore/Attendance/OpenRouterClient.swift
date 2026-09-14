import Foundation
import OSLog

/// Talks to OpenRouter for the complete unresolved matching problem after local
/// deterministic matches have been removed.
///
/// Failure here is always survivable: attendance recording and Auto Admit carry
/// on regardless, and unresolved names simply stay Needs Review.
public final class OpenRouterClient {
    public struct Configuration: Equatable {
        public var model: String
        public var timeout: TimeInterval
        public var maxAttempts: Int

        public init(
            model: String = "openai/gpt-4o-mini",
            timeout: TimeInterval = 30,
            maxAttempts: Int = 2
        ) {
            self.model = model
            self.timeout = timeout
            self.maxAttempts = max(1, min(maxAttempts, 3))
        }
    }

    public static let endpoint = URL(string: "https://openrouter.ai/api/v1/chat/completions")!

    private let logger = Logger(subsystem: "com.mohamedhosam.ZoomAutoAdmit", category: "ai-matching")
    private let configuration: Configuration
    private let session: URLSession
    private let apiKeyProvider: () -> String?

    public init(
        configuration: Configuration = Configuration(),
        session: URLSession = .shared,
        apiKeyProvider: @escaping () -> String? = { APIKeyStore.load() }
    ) {
        self.configuration = configuration
        self.session = session
        self.apiKeyProvider = apiKeyProvider
    }

    /// The model receives every unresolved student and every still-unclaimed
    /// observation together. No local similarity score filters candidate pairs.
    public static func prompt(for request: AIMatchRequest) -> String {
        let students = request.students
            .map { "  {\"id\": \"\($0.id)\", \"name\": \"\(escape($0.officialName))\"\($0.alreadyPresent ? ", \"already_present\": true" : "")}" }
            .joined(separator: ",\n")
        let names = request.observedNames
            .map { "  {\"id\": \"\($0.id)\", \"name\": \"\(escape($0.displayName))\"}" }
            .joined(separator: ",\n")

        return """
        You match official student names to the display names people used in a Zoom meeting.

        Compare the complete student list and complete observed-name list globally before assigning anything.

        Names may contain Arabic instead of English, English instead of Arabic, Arabic/English \
        transliteration, spelling variations, only a first name, first name plus surname, missing \
        middle names, reordered names, nicknames, abbreviations, capitalization differences, or \
        extra Zoom/device text. Titles may include Dr, Prof, Eng, Mr, Mrs, Ms, د, دكتور, دكتورة, \
        م, or مهندس. For example, رفيق may correspond to Rafeek/Rafiq/Rafik; محمد to \
        Mohamed/Mohammad/Muhammad; أيمن to Ayman/Aiman; and وفاء to Wafaa/Wafa.

        Rules:
        - Only use the opaque student and observed-name IDs given below. Never invent an ID or identity.
        - Each observed name may appear at most once. A student MAY appear more than once: the same \
        student often joins twice (phone and laptop, or leaving and rejoining) under the same or a \
        very close name, e.g. "Amir Girges" and "amir abdu", or "Zahraa Swelim" and "zahraa hagag \
        abdelsamea". Pair every observed name that is clearly that same person with the student.
        - Students marked "already_present": true were already matched on another Zoom name. Pair an \
        observed name with them only when it is clearly a second identity of that same person, and \
        never when a student without that mark fits it about as well.
        - Consider competing candidates before assigning. A shared first name with multiple plausible \
        students is ambiguous and must be marked needs_review or left unmatched, never chosen randomly.
        - The opposite case is a confident match, not a doubtful one. When a name element points at \
        exactly one student on this list and no other student competes for it, assign it. A surname \
        that differs, is missing, is extra, or is reordered does not weaken a pairing that is already \
        unique: people sign in with a married name, a family name, a shortened name, or only part of \
        their full name.
        - Judge ambiguity only against the students on this list. A given name that is common in the \
        wider world but belongs to exactly one student here is not ambiguous, and must not be \
        downgraded for being common.
        - Calibrate confidence to the competition, not to how unusual the spelling or transliteration is:
          - 0.90 to 1.00: the pairing is unique on this list and nothing else plausibly competes.
          - 0.70 to 0.89: plausible, but another student also competes for the same observed name, or \
        the only thing shared is one short or very common element.
          - below 0.70: guesswork. Leave it unmatched instead of proposing it.
        - Set needs_review to true only when a competing candidate genuinely exists. Do not set it \
        merely because part of the name is absent, transliterated, or spelled differently.
        - If there is no plausible pairing, leave the student and observed name unmatched.

        Students:
        [
        \(students)
        ]

        Observed Zoom names:
        [
        \(names)
        ]

        Reply with JSON only, in exactly this shape:
        {"matches":[{"student_id":"s0","observed_name_id":"z0","confidence":0.95,\
        "needs_review":false,"reason":"..."}],"unmatched_student_ids":["s1"],\
        "unmatched_observed_name_ids":["z1"]}
        """
    }

    /// Everything about one exchange, so what was asked and what came back can
    /// both be shown.
    ///
    /// "It matched nothing" is not a useful answer by itself: the question is
    /// always whether the name was even sent, and what the model said about it.
    public struct Exchange {
        public let request: AIMatchRequest
        /// The exact user message that was sent.
        public let prompt: String
        /// The model's reply, verbatim, before any parsing.
        public let rawResponse: String?
        public let response: AIMatchResponse?
        public let error: AIMatchError?
        public let httpStatus: Int?
        public let attempts: Int

        public var succeeded: Bool { response != nil }

        public init(
            request: AIMatchRequest,
            prompt: String,
            rawResponse: String? = nil,
            response: AIMatchResponse? = nil,
            error: AIMatchError? = nil,
            httpStatus: Int? = nil,
            attempts: Int = 0
        ) {
            self.request = request
            self.prompt = prompt
            self.rawResponse = rawResponse
            self.response = response
            self.error = error
            self.httpStatus = httpStatus
            self.attempts = attempts
        }
    }

    /// Sends the request. Never logs the key or the Authorization header.
    public func proposeMatches(for request: AIMatchRequest) async -> Exchange {
        let prompt = Self.prompt(for: request)

        guard request.isWorthSending else {
            return Exchange(
                request: request,
                prompt: prompt,
                response: AIMatchResponse(
                    matches: [],
                    unmatchedStudentIDs: request.students.map(\.id),
                    unmatchedObservedNameIDs: request.observedNames.map(\.id)
                )
            )
        }
        guard let apiKey = apiKeyProvider(), !apiKey.isEmpty else {
            return Exchange(request: request, prompt: prompt, error: .noAPIKey)
        }

        let body: [String: Any] = [
            "model": configuration.model,
            "temperature": 0,
            "response_format": ["type": "json_object"],
            "messages": [
                ["role": "system", "content": "You reply with JSON only."],
                ["role": "user", "content": prompt]
            ]
        ]

        guard let payload = try? JSONSerialization.data(withJSONObject: body) else {
            return Exchange(
                request: request,
                prompt: prompt,
                error: .malformedResponse("request could not be encoded")
            )
        }

        var urlRequest = URLRequest(url: Self.endpoint)
        urlRequest.httpMethod = "POST"
        urlRequest.timeoutInterval = configuration.timeout
        urlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        urlRequest.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        urlRequest.httpBody = payload

        var lastError: AIMatchError = .network("no attempt was made")
        var lastRaw: String?
        var lastStatus: Int?
        var used = 0

        for attempt in 1...configuration.maxAttempts {
            used = attempt
            // Only counts and the model name are logged; never names, never the key.
            logger.info("AI matching attempt \(attempt) students=\(request.students.count) names=\(request.observedNames.count)")
            do {
                let (data, response) = try await session.data(for: urlRequest)
                guard let http = response as? HTTPURLResponse else {
                    lastError = .network("no HTTP response")
                    continue
                }
                lastStatus = http.statusCode
                guard (200..<300).contains(http.statusCode) else {
                    lastError = .httpStatus(http.statusCode)
                    lastRaw = String(data: data, encoding: .utf8)
                    // Client-side errors will not improve on a retry.
                    if (400..<500).contains(http.statusCode), http.statusCode != 429 { break }
                    continue
                }
                guard let content = Self.extractContent(from: data) else {
                    lastError = .malformedResponse("no message content")
                    lastRaw = String(data: data, encoding: .utf8)
                    continue
                }
                lastRaw = content
                do {
                    return Exchange(
                        request: request,
                        prompt: prompt,
                        rawResponse: content,
                        response: try AIMatchValidator.decode(content),
                        httpStatus: http.statusCode,
                        attempts: attempt
                    )
                } catch let error as AIMatchError {
                    lastError = error
                    continue
                } catch {
                    lastError = .malformedResponse("\(error)")
                    continue
                }
            } catch {
                lastError = .network(error.localizedDescription)
            }
        }

        logger.notice("AI matching failed: \(lastError.message, privacy: .public)")
        return Exchange(
            request: request,
            prompt: prompt,
            rawResponse: lastRaw,
            error: lastError,
            httpStatus: lastStatus,
            attempts: used
        )
    }

    /// Pulls `choices[0].message.content` out of an OpenAI-shaped reply.
    static func extractContent(from data: Data) -> String? {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let choices = root["choices"] as? [[String: Any]],
              let message = choices.first?["message"] as? [String: Any],
              let content = message["content"] as? String else {
            return nil
        }
        return content
    }

    private static func escape(_ value: String) -> String {
        value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: " ")
    }
}
