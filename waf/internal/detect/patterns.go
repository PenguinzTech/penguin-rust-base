package detect

import (
	"fmt"
	"math"
	"net"
	"sync"
	"time"
)

// Detection represents a detected anomaly.
type Detection struct {
	Heuristic string
	IP        net.IP
	SteamID   uint64
	Detail    string
}

// PatternDetector analyzes packets for Rust-specific network anomalies.
type PatternDetector struct {
	aimbotCV float64
	timing   sync.Map // key: string(ip) → *timingRecord
	sizes    sync.Map // key: string(ip) → *sizeRecord
}

type timingRecord struct {
	lastSeen  time.Time
	intervals []float64 // rolling last 20 inter-packet intervals in ms
	mu        sync.Mutex
}

type sizeRecord struct {
	sizes []int // rolling last 50 packet sizes
	mu    sync.Mutex
}

// NewPatternDetector creates a new PatternDetector.
// aimbotCV is the coefficient of variation threshold below which aimbot is suspected.
// Set to 0 to disable aimbot detection.
func NewPatternDetector(aimbotCV float64) *PatternDetector {
	return &PatternDetector{
		aimbotCV: aimbotCV,
	}
}

// Inspect analyzes a packet and returns any detections.
// Never blocks traffic; all detections are LOG/ALERT level.
func (p *PatternDetector) Inspect(ip net.IP, steamID uint64, payload []byte) []Detection {
	if ip == nil {
		return []Detection{}
	}

	detections := []Detection{}

	ipStr := ip.String()

	// Heuristic 1: Aimbot timing
	if p.aimbotCV > 0 {
		if det := p.checkAimbotTiming(ipStr, steamID); det != nil {
			detections = append(detections, *det)
		}
	}

	// Heuristic 2: Packet size anomaly
	if det := p.checkSizeAnomaly(ipStr, steamID, len(payload)); det != nil {
		detections = append(detections, *det)
	}

	return detections
}

// checkAimbotTiming checks for suspiciously low variation in inter-packet intervals.
func (p *PatternDetector) checkAimbotTiming(ipStr string, steamID uint64) *Detection {
	now := time.Now()

	val, _ := p.timing.LoadOrStore(ipStr, &timingRecord{
		lastSeen:  now,
		intervals: []float64{},
	})

	record := val.(*timingRecord)
	record.mu.Lock()
	defer record.mu.Unlock()

	// Calculate interval since last packet (in milliseconds)
	interval := now.Sub(record.lastSeen).Seconds() * 1000.0
	record.lastSeen = now

	// Append interval to rolling window (max 20)
	record.intervals = append(record.intervals, interval)
	if len(record.intervals) > 20 {
		record.intervals = record.intervals[1:]
	}

	// Need at least 10 intervals to compute statistics
	if len(record.intervals) < 10 {
		return nil
	}

	// Compute mean and standard deviation
	mean := computeMean(record.intervals)
	if mean == 0 {
		return nil
	}

	stddev := computeStdDev(record.intervals, mean)
	cv := stddev / mean

	// If CV is below threshold and threshold is enabled, flag as potential aimbot
	if cv < p.aimbotCV {
		return &Detection{
			Heuristic: "aimbot_timing",
			IP:        net.ParseIP(ipStr),
			SteamID:   steamID,
			Detail:    fmt.Sprintf("coefficient of variation: %.4f (threshold: %.4f)", cv, p.aimbotCV),
		}
	}

	return nil
}

// checkSizeAnomaly checks for packet sizes that deviate >3σ from the mean.
func (p *PatternDetector) checkSizeAnomaly(ipStr string, steamID uint64, size int) *Detection {
	val, _ := p.sizes.LoadOrStore(ipStr, &sizeRecord{
		sizes: []int{},
	})

	record := val.(*sizeRecord)
	record.mu.Lock()
	defer record.mu.Unlock()

	// Append size to rolling window (max 50)
	record.sizes = append(record.sizes, size)
	if len(record.sizes) > 50 {
		record.sizes = record.sizes[1:]
	}

	// Need at least 20 sizes to compute statistics
	if len(record.sizes) < 20 {
		return nil
	}

	// Compute mean and standard deviation
	mean := computeMeanInt(record.sizes)
	if mean == 0 {
		return nil
	}

	stddev := computeStdDevInt(record.sizes, mean)

	// Check if current size deviates >3σ from mean
	deviation := math.Abs(float64(size) - float64(mean))
	if deviation > 3.0*stddev {
		return &Detection{
			Heuristic: "size_anomaly",
			IP:        net.ParseIP(ipStr),
			SteamID:   steamID,
			Detail:    fmt.Sprintf("size %d, mean %.1f, stddev %.1f (deviation: %.2fσ)", size, mean, stddev, deviation/stddev),
		}
	}

	return nil
}

// computeMean calculates the arithmetic mean of a float64 slice.
func computeMean(values []float64) float64 {
	if len(values) == 0 {
		return 0
	}

	sum := 0.0
	for _, v := range values {
		sum += v
	}

	return sum / float64(len(values))
}

// computeStdDev calculates the standard deviation of a float64 slice given the mean.
func computeStdDev(values []float64, mean float64) float64 {
	if len(values) <= 1 {
		return 0
	}

	sumSquaredDiff := 0.0
	for _, v := range values {
		diff := v - mean
		sumSquaredDiff += diff * diff
	}

	variance := sumSquaredDiff / float64(len(values)-1)
	return math.Sqrt(variance)
}

// computeMeanInt calculates the arithmetic mean of an int slice.
func computeMeanInt(values []int) float64 {
	if len(values) == 0 {
		return 0
	}

	sum := 0
	for _, v := range values {
		sum += v
	}

	return float64(sum) / float64(len(values))
}

// computeStdDevInt calculates the standard deviation of an int slice given the mean.
func computeStdDevInt(values []int, mean float64) float64 {
	if len(values) <= 1 {
		return 0
	}

	sumSquaredDiff := 0.0
	for _, v := range values {
		diff := float64(v) - mean
		sumSquaredDiff += diff * diff
	}

	variance := sumSquaredDiff / float64(len(values)-1)
	return math.Sqrt(variance)
}
