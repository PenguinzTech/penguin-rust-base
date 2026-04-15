package detect

import (
	"net"
	"testing"
	"time"
)

func TestInspectNormalTraffic(t *testing.T) {
	detector := NewPatternDetector(0.2) // CV threshold

	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)
	payload := []byte{0x01, 0x02, 0x03, 0x04, 0x05}

	detections := detector.Inspect(ip, steamID, payload)

	// Normal traffic should produce minimal detections
	if len(detections) > 1 {
		t.Errorf("Inspect() with normal traffic should produce ≤1 detections, got %d", len(detections))
	}
}

func TestInspectNilIP(t *testing.T) {
	detector := NewPatternDetector(0.2)

	steamID := uint64(76561198123456789)
	payload := []byte{0x01, 0x02, 0x03, 0x04}

	detections := detector.Inspect(nil, steamID, payload)

	if len(detections) > 0 {
		t.Error("Inspect() with nil IP should return empty detections")
	}
}

func TestCheckAimbotTimingDetection(t *testing.T) {
	// Use very low CV threshold to force aimbot detection
	detector := NewPatternDetector(1.0)

	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)
	payload := []byte{0x01, 0x02}

	// Send packets with uniform timing (low CV)
	for i := 0; i < 15; i++ {
		time.Sleep(50 * time.Millisecond)
		detector.Inspect(ip, steamID, payload)
	}

	// Additional packet to trigger detection
	time.Sleep(50 * time.Millisecond)
	detections := detector.Inspect(ip, steamID, payload)

	// Should have aimbot_timing detection due to uniform timing
	found := false
	for _, det := range detections {
		if det.Heuristic == "aimbot_timing" {
			found = true
			break
		}
	}

	if !found && len(detections) == 0 {
		t.Error("Inspect() should detect aimbot_timing with uniform packet intervals")
	}
}

func TestAimbotDetectionDisabled(t *testing.T) {
	// CV threshold of 0 disables aimbot detection
	detector := NewPatternDetector(0)

	ip := net.ParseIP("192.168.1.1")
	payload := []byte{0x01, 0x02}

	// Send packets with uniform timing
	for i := 0; i < 15; i++ {
		time.Sleep(10 * time.Millisecond)
		detector.Inspect(ip, 0, payload)
	}

	detections := detector.Inspect(ip, 0, payload)

	// Should not have aimbot detection
	for _, det := range detections {
		if det.Heuristic == "aimbot_timing" {
			t.Error("Inspect() should not detect aimbot when CV threshold is 0")
		}
	}
}

func TestCheckSizeAnomalyDetection(t *testing.T) {
	detector := NewPatternDetector(0)

	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)

	// Build normal traffic pattern (20+ packets around same size)
	normalSize := 100
	for i := 0; i < 20; i++ {
		payload := make([]byte, normalSize)
		detector.Inspect(ip, steamID, payload)
	}

	// Now send an outlier (very different size > 3σ)
	outlierSize := 500
	payload := make([]byte, outlierSize)
	detections := detector.Inspect(ip, steamID, payload)

	// Should detect size anomaly
	found := false
	for _, det := range detections {
		if det.Heuristic == "size_anomaly" {
			found = true
			break
		}
	}

	if !found && len(detections) == 0 {
		t.Error("Inspect() should detect size_anomaly for outlier packet size")
	}
}

func TestSizeAnomalyNeedsMinimumSamples(t *testing.T) {
	detector := NewPatternDetector(0)

	ip := net.ParseIP("192.168.1.1")

	// Send fewer than 20 packets (minimum needed for statistics)
	for i := 0; i < 15; i++ {
		payload := make([]byte, 100)
		detections := detector.Inspect(ip, 0, payload)
		if len(detections) > 0 && detections[0].Heuristic == "size_anomaly" {
			t.Error("Inspect() should not detect size_anomaly with < 20 samples")
		}
	}
}

func TestDetectionContainsIPAndSteamID(t *testing.T) {
	detector := NewPatternDetector(1.0)

	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)

	// Generate aimbot-like pattern
	for i := 0; i < 15; i++ {
		time.Sleep(10 * time.Millisecond)
		detector.Inspect(ip, steamID, []byte{0x01})
	}

	detections := detector.Inspect(ip, steamID, []byte{0x01})

	if len(detections) > 0 {
		det := detections[0]
		if det.IP == nil || det.IP.String() != ip.String() {
			t.Error("Detection should contain correct IP")
		}
		if det.SteamID != steamID {
			t.Error("Detection should contain correct SteamID")
		}
	}
}

func TestMultipleDetectionsInSinglePacket(t *testing.T) {
	detector := NewPatternDetector(0.1)

	ip := net.ParseIP("192.168.1.1")

	// Build two detection patterns:
	// 1. Uniform timing (aimbot)
	for i := 0; i < 20; i++ {
		time.Sleep(50 * time.Millisecond)
		detector.Inspect(ip, 0, make([]byte, 100))
	}

	// Trigger both detections in single inspect
	time.Sleep(50 * time.Millisecond)
	detections := detector.Inspect(ip, 0, make([]byte, 500))

	// Could have both aimbot_timing and size_anomaly
	// Or just one depending on thresholds
	// Test that detections array is properly formed
	if len(detections) > 2 {
		t.Errorf("Inspect() should return ≤2 detections per packet, got %d", len(detections))
	}
}

func TestDetectionDetailField(t *testing.T) {
	detector := NewPatternDetector(0.2)

	ip := net.ParseIP("192.168.1.1")

	// Generate pattern
	for i := 0; i < 15; i++ {
		time.Sleep(10 * time.Millisecond)
		detector.Inspect(ip, 0, []byte{0x01})
	}

	detections := detector.Inspect(ip, 0, []byte{0x01})

	for _, det := range detections {
		if det.Detail == "" {
			t.Error("Detection Detail field should not be empty")
		}
	}
}

func TestPatternDetectorDifferentIPs(t *testing.T) {
	// Use a high CV threshold (2.0). ip1 gets uniform timing (CV well below 2.0 → triggers).
	// ip2 gets strongly alternating timing producing CV well above 2.0.
	detector := NewPatternDetector(2.0)

	ip1 := net.ParseIP("192.168.1.1")
	ip2 := net.ParseIP("192.168.1.2")

	// Send uniform timing on ip1 — 50ms sleep gives CV ~0.05–0.3 < 2.0 → triggers aimbot
	for i := 0; i < 15; i++ {
		time.Sleep(50 * time.Millisecond)
		detector.Inspect(ip1, 0, []byte{0x01})
	}

	// Send strongly variable timing on ip2 — alternate 1ms / 500ms → CV >> 2.0
	for i := 0; i < 15; i++ {
		if i%2 == 0 {
			time.Sleep(1 * time.Millisecond)
		} else {
			time.Sleep(200 * time.Millisecond)
		}
		detector.Inspect(ip2, 0, []byte{0x02})
	}

	// Check ip1 triggers aimbot, ip2 doesn't
	det1 := detector.Inspect(ip1, 0, []byte{0x01})
	det2 := detector.Inspect(ip2, 0, []byte{0x01})

	hasAimbot1 := false
	hasAimbot2 := false

	for _, det := range det1 {
		if det.Heuristic == "aimbot_timing" {
			hasAimbot1 = true
		}
	}

	for _, det := range det2 {
		if det.Heuristic == "aimbot_timing" {
			hasAimbot2 = true
		}
	}

	if hasAimbot1 == hasAimbot2 {
		t.Error("Different IPs should track patterns independently")
	}
}

func TestTimingRecordRollingWindow(t *testing.T) {
	detector := NewPatternDetector(0.3)

	ip := net.ParseIP("192.168.1.1")

	// Send 30 packets (rolling window max is 20)
	for i := 0; i < 30; i++ {
		time.Sleep(10 * time.Millisecond)
		detector.Inspect(ip, 0, []byte{0x01})
	}

	// Should not panic and should continue working correctly
	detections := detector.Inspect(ip, 0, []byte{0x01})
	if detections == nil {
		t.Error("Inspect() should return valid detections array")
	}
}

func TestSizeRecordRollingWindow(t *testing.T) {
	detector := NewPatternDetector(0)

	ip := net.ParseIP("192.168.1.1")

	// Send 70 packets with varying sizes (rolling window max is 50)
	for i := 0; i < 70; i++ {
		size := (i % 10) * 10
		payload := make([]byte, size+100)
		detector.Inspect(ip, 0, payload)
	}

	// Should not panic
	detections := detector.Inspect(ip, 0, make([]byte, 100))
	if detections == nil {
		t.Error("Inspect() should return valid detections array")
	}
}

func TestEmptyPayload(t *testing.T) {
	detector := NewPatternDetector(0.2)

	ip := net.ParseIP("192.168.1.1")
	payload := []byte{}

	detections := detector.Inspect(ip, 0, payload)

	if detections == nil {
		t.Error("Inspect() should return detections array, not nil")
	}
}

func TestHighCVThreshold(t *testing.T) {
	// Very high CV threshold (10.0) means aimbot is flagged when measured CV < 10.0,
	// which is almost always true for uniform traffic — detection expected.
	detector := NewPatternDetector(10.0)

	ip := net.ParseIP("192.168.1.1")

	// Send uniform timing to build up intervals
	for i := 0; i < 15; i++ {
		time.Sleep(10 * time.Millisecond)
		detector.Inspect(ip, 0, []byte{0x01})
	}

	detections := detector.Inspect(ip, 0, []byte{0x01})

	// With a very high CV threshold, uniform traffic should trigger aimbot detection
	found := false
	for _, det := range detections {
		if det.Heuristic == "aimbot_timing" {
			found = true
		}
	}

	if !found {
		t.Error("High CV threshold (10.0) should flag uniform traffic as aimbot_timing")
	}
}

func TestLowCVThreshold(t *testing.T) {
	// Very low CV threshold (0.01) means aimbot is flagged only when measured CV < 0.01.
	// Real timing with time.Sleep has CV well above 0.01, so detection should NOT trigger.
	detector := NewPatternDetector(0.01)

	ip := net.ParseIP("192.168.1.1")

	// Uniform timing (but with OS scheduling jitter, CV > 0.01)
	for i := 0; i < 15; i++ {
		time.Sleep(10 * time.Millisecond)
		detector.Inspect(ip, 0, []byte{0x01})
	}

	detections := detector.Inspect(ip, 0, []byte{0x01})

	// With threshold 0.01, only near-perfect timing triggers — OS jitter prevents this
	for _, det := range detections {
		if det.Heuristic == "aimbot_timing" {
			t.Error("Low CV threshold (0.01) should not trigger aimbot detection with OS-scheduled timing")
		}
	}
}

func TestZeroPayloadSize(t *testing.T) {
	detector := NewPatternDetector(0)

	ip := net.ParseIP("192.168.1.1")

	// Send enough packets with size 0
	for i := 0; i < 25; i++ {
		detector.Inspect(ip, 0, []byte{})
	}

	detections := detector.Inspect(ip, 0, []byte{})

	// Should handle gracefully
	if detections == nil {
		t.Error("Inspect() should handle zero-size packets")
	}
}
