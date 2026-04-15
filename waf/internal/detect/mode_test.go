package detect

import (
	"testing"
)

func TestParseDetectorMode(t *testing.T) {
	tests := []struct {
		name     string
		input    string
		expected DetectorMode
	}{
		{"off string", "off", ModeOff},
		{"monitor string", "monitor", ModeMonitor},
		{"block string", "block", ModeBlock},
		{"uppercase BLOCK", "BLOCK", ModeBlock},
		{"uppercase MONITOR", "MONITOR", ModeMonitor},
		{"uppercase OFF", "OFF", ModeOff},
		{"mixed case Block", "Block", ModeBlock},
		{"empty string", "", ModeMonitor},
		{"whitespace only", "   ", ModeMonitor},
		{"whitespace padded monitor", "  monitor  ", ModeMonitor},
		{"unknown value", "unknown", ModeMonitor},
		{"garbage", "xyz", ModeMonitor},
		{"invalid block", "notblock", ModeMonitor},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := ParseDetectorMode(tt.input)
			if got != tt.expected {
				t.Errorf("ParseDetectorMode(%q) = %v, want %v", tt.input, got, tt.expected)
			}
		})
	}
}
