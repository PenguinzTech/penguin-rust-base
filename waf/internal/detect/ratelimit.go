package detect

import (
	"fmt"
	"net"
	"sync"
	"time"
)

// RateLimiter implements a token-bucket rate limiter, one bucket per key.
type RateLimiter struct {
	pps     float64  // max packets per second
	buckets sync.Map // key: string → *bucket
}

type bucket struct {
	tokens    float64
	lastRefil time.Time
	mu        sync.Mutex
}

// NewRateLimiter creates a new RateLimiter with the given max packets per second.
func NewRateLimiter(pps float64) *RateLimiter {
	return &RateLimiter{
		pps: pps,
	}
}

// Allow checks if a packet from the given key should be forwarded.
// Returns true if allowed, false if rate limit exceeded.
// key is either an IP string or a SteamID string.
func (r *RateLimiter) Allow(key string) bool {
	if r.pps <= 0 {
		// Rate limiting disabled
		return true
	}

	val, _ := r.buckets.LoadOrStore(key, &bucket{
		tokens:    r.pps,
		lastRefil: time.Now(),
	})

	b := val.(*bucket)
	b.mu.Lock()
	defer b.mu.Unlock()

	// Refill tokens based on elapsed time
	now := time.Now()
	elapsed := now.Sub(b.lastRefil).Seconds()
	b.tokens += elapsed * r.pps
	b.lastRefil = now

	// Cap tokens at the max rate
	if b.tokens > r.pps {
		b.tokens = r.pps
	}

	// Check if we have a token
	if b.tokens >= 1.0 {
		b.tokens -= 1.0
		return true
	}

	return false
}

// AllowPacket checks both ip and steamID (if non-zero).
// Returns false if either IP or SteamID is over the rate limit.
func (r *RateLimiter) AllowPacket(ip net.IP, steamID uint64) bool {
	if ip == nil {
		return true
	}

	// Check IP limit
	if !r.Allow(ip.String()) {
		return false
	}

	// Check SteamID limit if non-zero
	if steamID != 0 {
		// Use a simple string key for the SteamID
		steamKey := stringFromUint64(steamID)
		if !r.Allow(steamKey) {
			return false
		}
	}

	return true
}

// stringFromUint64 converts a uint64 to a string key for rate limiting.
func stringFromUint64(id uint64) string {
	return fmt.Sprintf("steam:%d", id)
}
