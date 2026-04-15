package detect

import (
	"bytes"
	"math"
	"testing"
)

func TestEntropyDetector_LowEntropyNotFlagged(t *testing.T) {
	detector := NewEntropyDetector(ModeBlock, 3.0)
	payload := bytes.Repeat([]byte{0xAA}, 100)
	result := detector.Analyze(payload)
	if result {
		t.Errorf("low entropy payload should not be flagged, got true")
	}
}

func TestEntropyDetector_HighEntropyFlagged(t *testing.T) {
	// Create payload with all 256 byte values (entropy = 8.0)
	payload := make([]byte, 256)
	for i := 0; i < 256; i++ {
		payload[i] = byte(i)
	}
	detector := NewEntropyDetector(ModeBlock, 7.0)
	result := detector.Analyze(payload)
	if !result {
		t.Errorf("high entropy payload should be flagged, got false")
	}
}

func TestEntropyDetector_EmptyPayload(t *testing.T) {
	detector := NewEntropyDetector(ModeBlock, 1.0)
	// Test nil
	result := detector.Analyze(nil)
	if result {
		t.Errorf("nil payload should return false, got true")
	}
	// Test empty slice
	result = detector.Analyze([]byte{})
	if result {
		t.Errorf("empty payload should return false, got true")
	}
}

func TestEntropyDetector_Disabled(t *testing.T) {
	// High entropy payload
	payload := make([]byte, 256)
	for i := 0; i < 256; i++ {
		payload[i] = byte(i)
	}
	// Test threshold = 0 (disabled)
	detector := NewEntropyDetector(ModeBlock, 0)
	result := detector.Analyze(payload)
	if result {
		t.Errorf("disabled detector (threshold=0) should return false, got true")
	}
	// Test negative threshold (disabled)
	detector = NewEntropyDetector(ModeBlock, -1.0)
	result = detector.Analyze(payload)
	if result {
		t.Errorf("disabled detector (threshold<0) should return false, got true")
	}
}

func TestEntropyDetector_AtThreshold(t *testing.T) {
	// Create payload with all 256 byte values (entropy = 8.0)
	payload := make([]byte, 256)
	for i := 0; i < 256; i++ {
		payload[i] = byte(i)
	}
	// Threshold exactly equal to entropy (8.0)
	detector := NewEntropyDetector(ModeBlock, 8.0)
	result := detector.Analyze(payload)
	if result {
		t.Errorf("entropy equal to threshold should not be flagged (strictly greater), got true")
	}
}

func TestEntropyDetector_ModeOff(t *testing.T) {
	payload := make([]byte, 256)
	for i := range payload {
		payload[i] = byte(i)
	}
	d := NewEntropyDetector(ModeOff, 1.0)
	if d.Analyze(payload) {
		t.Fatal("ModeOff should never trigger")
	}
	if d.Mode() != ModeOff {
		t.Fatal("Mode() should return ModeOff")
	}
}

func TestEntropyDetector_UniformDistribution(t *testing.T) {
	// Create payload with all 256 byte values exactly once
	payload := make([]byte, 256)
	for i := 0; i < 256; i++ {
		payload[i] = byte(i)
	}

	// Verify entropy is 8.0 manually
	freqMap := make(map[byte]int)
	for _, b := range payload {
		freqMap[b]++
	}
	var entropy float64
	for _, count := range freqMap {
		if count > 0 {
			p := float64(count) / float64(len(payload))
			entropy -= p * math.Log2(p)
		}
	}

	// Should be very close to 8.0 (within floating point precision)
	expectedEntropy := 8.0
	if math.Abs(entropy-expectedEntropy) > 0.0001 {
		t.Errorf("uniform distribution entropy = %f, expected %f", entropy, expectedEntropy)
	}

	// Test flagging with threshold < 8.0
	detector := NewEntropyDetector(ModeBlock, 7.99)
	result := detector.Analyze(payload)
	if !result {
		t.Errorf("entropy 8.0 with threshold 7.99 should be flagged, got false")
	}

	// Test not flagging with threshold > 8.0
	detector = NewEntropyDetector(ModeBlock, 8.01)
	result = detector.Analyze(payload)
	if result {
		t.Errorf("entropy 8.0 with threshold 8.01 should not be flagged, got true")
	}
}
