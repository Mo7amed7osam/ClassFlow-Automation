import Foundation

/// One data row of a recordings tab. Columns are fixed: A File Name, B Type, C Date, D Shared Link.
public struct RecordingSheetRow: Equatable {
    public var tab: String
    public var rowNumber: Int
    public var fileName: String
    public var type: String
    /// yyyy-MM-dd, or nil when column C could not be read as a date.
    public var date: String?
    public var rawDate: String
    /// The Google Drive file link, or nil when column D holds no usable Drive link.
    public var driveURL: String?
    public var rawLink: String

    public init(tab: String, rowNumber: Int, fileName: String, type: String, date: String?, rawDate: String, driveURL: String?, rawLink: String) {
        self.tab = tab
        self.rowNumber = rowNumber
        self.fileName = fileName
        self.type = type
        self.date = date
        self.rawDate = rawDate
        self.driveURL = driveURL
        self.rawLink = rawLink
    }
}

public struct RecordingSheetTab: Equatable {
    public var title: String
    public var rows: [RecordingSheetRow]
}

/// Reads the spreadsheet as Google Sheets returns it with `includeGridData`.
///
/// Each cell carries its displayed text, its underlying value and any link on it, so a date typed
/// as a real date (a serial number underneath) and a Drive link pasted as a smart chip both read
/// correctly without guessing from how they happen to be displayed.
public enum RecordingSheetParser {
    public static func parse(spreadsheetJSON data: Data) throws -> [RecordingSheetTab] {
        struct Spreadsheet: Decodable {
            struct Sheet: Decodable {
                struct Properties: Decodable { let title: String }
                struct Grid: Decodable { let rowData: [Row]? }
                struct Row: Decodable { let values: [Cell]? }
                let properties: Properties
                let data: [Grid]?
            }
            let sheets: [Sheet]?
        }
        let spreadsheet = try JSONDecoder().decode(Spreadsheet.self, from: data)
        return (spreadsheet.sheets ?? []).map { sheet in
            let rows = (sheet.data?.first?.rowData ?? []).enumerated().compactMap { index, row -> RecordingSheetRow? in
                parseRow(tab: sheet.properties.title, rowNumber: index + 1, cells: row.values ?? [])
            }
            return RecordingSheetTab(title: sheet.properties.title, rows: rows)
        }
    }

    public struct Cell: Decodable, Equatable {
        public struct Value: Decodable, Equatable {
            public let numberValue: Double?
            public let stringValue: String?
        }
        public struct Chip: Decodable, Equatable {
            public struct Properties: Decodable, Equatable {
                public let richLinkProperties: RichLink?
            }
            public struct RichLink: Decodable, Equatable { public let uri: String? }
            public let chipProperties: Properties?
        }
        public let formattedValue: String?
        public let effectiveValue: Value?
        public let hyperlink: String?
        public let chipRuns: [Chip]?

        public init(formattedValue: String?, numberValue: Double? = nil, hyperlink: String? = nil) {
            self.formattedValue = formattedValue
            effectiveValue = numberValue.map { Value(numberValue: $0, stringValue: nil) }
            self.hyperlink = hyperlink
            chipRuns = nil
        }

        var linkCandidates: [String] {
            let chips = (chipRuns ?? []).compactMap { $0.chipProperties?.richLinkProperties?.uri }
            return ([hyperlink] + chips + [formattedValue, effectiveValue?.stringValue]).compactMap { $0?.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
        }
    }

    static func parseRow(tab: String, rowNumber: Int, cells: [Cell]) -> RecordingSheetRow? {
        func text(_ index: Int) -> String {
            guard cells.indices.contains(index) else { return "" }
            return (cells[index].formattedValue ?? cells[index].effectiveValue?.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        let fileName = text(0), type = text(1), rawDate = text(2)
        let linkCell = cells.indices.contains(3) ? cells[3] : nil
        let rawLink = linkCell?.linkCandidates.first ?? ""
        if [fileName, type, rawDate, rawLink].allSatisfy(\.isEmpty) { return nil }
        // The header row, wherever the sheet puts it.
        if fileName.lowercased() == "file name" || rawLink.lowercased() == "shared link" || rawDate.lowercased() == "date" { return nil }

        let dateCell = cells.indices.contains(2) ? cells[2] : nil
        let date = dateCell.flatMap { cell in
            cell.effectiveValue?.numberValue.flatMap(dateFromSerial) ?? parseDateText(rawDate)
        }
        let drive = linkCell?.linkCandidates.first(where: { RecordingLinkRules.isGoogleDriveFileLink($0) })
        return RecordingSheetRow(tab: tab, rowNumber: rowNumber, fileName: fileName, type: type, date: date, rawDate: rawDate, driveURL: drive, rawLink: rawLink)
    }

    /// Sheets serial dates count days from 1899-12-30.
    public static func dateFromSerial(_ serial: Double) -> String? {
        guard serial > 20_000, serial < 2_958_465 else { return nil }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        guard let epoch = calendar.date(from: DateComponents(year: 1899, month: 12, day: 30)),
              let date = calendar.date(byAdding: .day, value: Int(serial.rounded(.down)), to: epoch) else { return nil }
        let parts = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", parts.year!, parts.month!, parts.day!)
    }

    /// yyyy-MM-dd, d/M/yyyy (day first, as dates are written in Egypt), "12 Sep 2026", "Sep 12, 2026".
    /// A slash date whose first part is over 12 is read day first; one whose second part is over
    /// 12 is read month first; otherwise day first.
    public static func parseDateText(_ raw: String) -> String? {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        func valid(_ year: Int, _ month: Int, _ day: Int) -> String? {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = TimeZone(identifier: "UTC")!
            guard (1...12).contains(month), let date = calendar.date(from: DateComponents(year: year, month: month, day: day)),
                  calendar.component(.day, from: date) == day else { return nil }
            return String(format: "%04d-%02d-%02d", year, month, day)
        }
        let numbers = text.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
        if text.range(of: #"^\d{4}[-/.]\d{1,2}[-/.]\d{1,2}"#, options: .regularExpression) != nil, numbers.count >= 3 {
            return valid(numbers[0], numbers[1], numbers[2])
        }
        if text.range(of: #"^\d{1,2}[-/.]\d{1,2}[-/.]\d{4}"#, options: .regularExpression) != nil, numbers.count >= 3 {
            let (first, second, year) = (numbers[0], numbers[1], numbers[2])
            if second > 12 { return valid(year, first, second) }
            return valid(year, second, first)
        }
        let months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]
        let lower = text.lowercased()
        if let monthIndex = months.firstIndex(where: { lower.contains($0) }), numbers.count >= 2 {
            let year = numbers.first(where: { $0 > 1900 }) ?? 0
            let day = numbers.first(where: { $0 >= 1 && $0 <= 31 }) ?? 0
            return valid(year, monthIndex + 1, day)
        }
        return nil
    }
}

/// The same Drive link rules the helper applies, so a row is judged identically on both sides.
public enum RecordingLinkRules {
    public static func driveFileID(_ value: String) -> String? {
        let text = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, text.count <= 2048, text.rangeOfCharacter(from: .whitespacesAndNewlines) == nil,
              let url = URLComponents(string: text), url.scheme == "https", url.host?.lowercased() == "drive.google.com",
              url.port == nil, url.user == nil, url.password == nil else { return nil }
        let idPattern = #"^[A-Za-z0-9_-]{20,100}$"#
        let path = url.path
        if let match = path.range(of: #"^/file/d/[A-Za-z0-9_-]{20,100}(/(view|preview|edit))?/?$"#, options: .regularExpression) {
            let id = path[match].split(separator: "/")[2]
            return String(id)
        }
        if path == "/open", let id = url.queryItems?.first(where: { $0.name == "id" })?.value, id.range(of: idPattern, options: .regularExpression) != nil {
            return id
        }
        return nil
    }

    public static func isGoogleDriveFileLink(_ value: String) -> Bool { driveFileID(value) != nil }

    public static func preview(_ value: String) -> String {
        if let id = driveFileID(value) { return "drive.google.com/file/d/\(id.prefix(6))…" }
        return value.count <= 40 ? value : String(value.prefix(40)) + "…"
    }
}

/// Reads the spreadsheet through the Sheets API.
public final class GoogleSheetsClient {
    private let oauth: GoogleOAuthClient
    private let http: GoogleOAuthClient.HTTP

    public init(oauth: GoogleOAuthClient, http: @escaping GoogleOAuthClient.HTTP = GoogleOAuthClient.urlSession) {
        self.oauth = oauth
        self.http = http
    }

    public static func spreadsheetURL(id: String) -> URL? {
        var components = URLComponents(string: "https://sheets.googleapis.com/v4/spreadsheets/")
        components?.path += id.trimmingCharacters(in: .whitespacesAndNewlines)
        components?.queryItems = [
            URLQueryItem(name: "includeGridData", value: "true"),
            URLQueryItem(name: "fields", value: "properties(title),sheets(properties(title),data(rowData(values(formattedValue,effectiveValue,hyperlink,chipRuns(chip(richLinkProperties(uri)))))))")
        ]
        return components?.url
    }

    /// Every tab with its rows. Tabs are read dynamically; nothing about their names is assumed here.
    public func readTabs(spreadsheetID: String) async throws -> [RecordingSheetTab] {
        let data = try await get(spreadsheetID: spreadsheetID)
        return try RecordingSheetParser.parse(spreadsheetJSON: Self.normalizeChips(data))
    }

    private func get(spreadsheetID: String) async throws -> Data {
        guard let url = Self.spreadsheetURL(id: spreadsheetID), !spreadsheetID.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw GoogleSheetsError.missingSpreadsheetID
        }
        var request = URLRequest(url: url)
        request.setValue("Bearer \(try await oauth.accessToken())", forHTTPHeaderField: "Authorization")
        let (data, response) = try await http(request)
        switch response.statusCode {
        case 200: return data
        case 403: throw GoogleSheetsError.forbidden
        case 404: throw GoogleSheetsError.notFound
        default: throw GoogleSheetsError.http(response.statusCode, String(data: data, encoding: .utf8)?.prefix(200).description ?? "")
        }
    }

    /// The API nests chips as `chipRuns[].chip.richLinkProperties`; the parser reads them flatter.
    static func normalizeChips(_ data: Data) -> Data {
        guard var text = String(data: data, encoding: .utf8) else { return data }
        text = text.replacingOccurrences(of: "\"chip\":", with: "\"chipProperties\":")
        return Data(text.utf8)
    }
}

public enum GoogleSheetsError: Error, Equatable, LocalizedError {
    case missingSpreadsheetID
    case forbidden
    case notFound
    case http(Int, String)

    public var errorDescription: String? {
        switch self {
        case .missingSpreadsheetID: return "Enter the spreadsheet ID first."
        case .forbidden: return "The connected Google account cannot open this spreadsheet. Share it with that account."
        case .notFound: return "No spreadsheet has that ID."
        case .http(let status, let body): return "Google Sheets answered HTTP \(status): \(body)"
        }
    }
}
