package detect

import "math"

// EntropyDetector flags packets whose Shannon entropy exceeds a threshold.
// High entropy (near 8 bits/byte) suggests encrypted tunnels or randomised DoS payloads.
type EntropyDetector struct {
	mode      DetectorMode
	threshold float64 // bits per byte; typical range 0–8
}

// NewEntropyDetector creates a detector; threshold <= 0 disables it (always returns false).
func NewEntropyDetector(mode DetectorMode, threshold float64) *EntropyDetector {
	return &EntropyDetector{
		mode:      mode,
		threshold: threshold,
	}
}

// Mode returns the detector's current DetectorMode.
func (e *EntropyDetector) Mode() DetectorMode { return e.mode }

// Analyze returns true if the payload's Shannon entropy exceeds the threshold.
// Empty payload returns false.
func (e *EntropyDetector) Analyze(payload []byte) bool {
	if e.mode == ModeOff {
		return false
	}
	// Disabled if threshold <= 0
	if e.threshold <= 0 {
		return false
	}

	// Empty payload never flagged
	if len(payload) == 0 {
		return false
	}

	// Calculate Shannon entropy
	entropy := e.calculateEntropy(payload)

	// Return true if entropy exceeds threshold (strictly greater than)
	return entropy > e.threshold
}

// calculateEntropy computes Shannon entropy in bits per byte.
// Formula: H = -sum(p * log2(p)) for each byte value (0-255) where p = count/len(payload)
// Only includes byte values that actually appear.
func (e *EntropyDetector) calculateEntropy(payload []byte) float64 {
	// Count frequency of each byte value using fixed array (no heap allocation)
	var freq [256]int
	for _, b := range payload {
		freq[b]++
	}

	// Calculate Shannon entropy
	var entropy float64
	length := float64(len(payload))
	for _, count := range freq[:] {
		if count > 0 {
			p := float64(count) / length
			entropy -= p * math.Log2(p)
		}
	}

	return entropy
}
