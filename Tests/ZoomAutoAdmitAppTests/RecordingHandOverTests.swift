import XCTest
@testable import ZoomAutoAdmitApp
import ZoomAutoAdmitCore

final class RecordingHandOverTests: XCTestCase {
    func testTheZoomRecordingIsTriedUntilEightTheNextMorningInCairo() {
        let handOver = AutomationCoordinator.recordingHandOver(sessionDate: "2026-09-14")
        var cairo = Calendar(identifier: .gregorian)
        cairo.timeZone = TimeZone(identifier: "Africa/Cairo")!
        let parts = cairo.dateComponents([.year, .month, .day, .hour, .minute], from: handOver)
        XCTAssertEqual([parts.year, parts.month, parts.day, parts.hour, parts.minute], [2026, 9, 15, 8, 0])
    }
}
