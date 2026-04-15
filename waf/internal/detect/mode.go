package detect

import "strings"

// DetectorMode controls how a detector's findings are acted upon.
type DetectorMode uint8

const (
	// ModeOff disables the detector entirely — RecordX/Analyze calls are no-ops.
	ModeOff DetectorMode = iota
	// ModeMonitor logs/alerts on findings but does not block traffic.
	ModeMonitor
	// ModeBlock drops or blocks traffic on findings.
	ModeBlock
)

// ParseDetectorMode converts a string to a DetectorMode.
// Accepts "off", "monitor", "block" (case-insensitive).
// Unknown values return ModeOff.
func ParseDetectorMode(s string) DetectorMode {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "monitor":
		return ModeMonitor
	case "block":
		return ModeBlock
	default:
		return ModeOff
	}
}
