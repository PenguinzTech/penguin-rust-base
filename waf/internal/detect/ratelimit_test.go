package detect

import (
	"fmt"
	"net"
	"testing"
	"time"
)

func TestAllowFirstPacket(t *testing.T) {
	limiter := NewRateLimiter(10.0) // 10 pps

	// First packet should always be allowed
	if !limiter.Allow("test_key") {
		t.Error("Allow() should allow first packet")
	}
}

func TestAllowWithinBudget(t *testing.T) {
	limiter := NewRateLimiter(10.0) // 10 pps

	key := "test_key"
	allowed := 0

	// Should allow first 10 packets within 1 second
	for i := 0; i < 15; i++ {
		if limiter.Allow(key) {
			allowed++
		}
	}

	if allowed != 10 {
		t.Errorf("Allow() allowed %d packets, want ~10", allowed)
	}
}

func TestAllowExceedsRateLimit(t *testing.T) {
	limiter := NewRateLimiter(5.0) // 5 pps

	key := "test_key"

	// Allow 5 packets
	for i := 0; i < 5; i++ {
		if !limiter.Allow(key) {
			t.Errorf("Allow() should allow packet %d", i+1)
		}
	}

	// 6th packet should be dropped
	if limiter.Allow(key) {
		t.Error("Allow() should drop packet exceeding rate limit")
	}
}

func TestAllowRateLimitDisabled(t *testing.T) {
	limiter := NewRateLimiter(0) // disabled

	key := "test_key"

	// All packets should be allowed
	for i := 0; i < 100; i++ {
		if !limiter.Allow(key) {
			t.Errorf("Allow() should allow all packets when rate limiting disabled, failed at packet %d", i+1)
		}
	}
}

func TestAllowNegativeRateLimit(t *testing.T) {
	limiter := NewRateLimiter(-5.0) // negative = disabled

	key := "test_key"

	// All packets should be allowed
	for i := 0; i < 100; i++ {
		if !limiter.Allow(key) {
			t.Errorf("Allow() should allow all packets with negative rate limit")
		}
	}
}

func TestAllowDifferentKeys(t *testing.T) {
	limiter := NewRateLimiter(3.0) // 3 pps per key

	key1 := "key1"
	key2 := "key2"

	// Each key should have its own bucket
	for i := 0; i < 3; i++ {
		if !limiter.Allow(key1) {
			t.Errorf("Allow() for key1 packet %d should succeed", i+1)
		}
	}

	for i := 0; i < 3; i++ {
		if !limiter.Allow(key2) {
			t.Errorf("Allow() for key2 packet %d should succeed", i+1)
		}
	}

	// Both should be at limit
	if limiter.Allow(key1) {
		t.Error("Allow() for key1 should be at limit")
	}

	if limiter.Allow(key2) {
		t.Error("Allow() for key2 should be at limit")
	}
}

func TestAllowTokenRefill(t *testing.T) {
	limiter := NewRateLimiter(10.0) // 10 pps

	key := "test_key"

	// Consume all tokens
	for i := 0; i < 10; i++ {
		limiter.Allow(key)
	}

	// Next should fail
	if limiter.Allow(key) {
		t.Error("Allow() should fail after exhausting tokens")
	}

	// Wait a bit and try again - should refill approximately
	time.Sleep(110 * time.Millisecond)

	// Should have at least 1 token now
	if !limiter.Allow(key) {
		t.Error("Allow() should succeed after token refill")
	}
}

func TestAllowPacketWithIP(t *testing.T) {
	tests := []struct {
		name    string
		ip      net.IP
		steamID uint64
		expect  bool
	}{
		{
			name:    "ip within budget",
			ip:      net.ParseIP("192.168.1.1"),
			steamID: 0,
			expect:  true,
		},
		{
			name:    "ip and steamid both within budget",
			ip:      net.ParseIP("10.0.0.1"),
			steamID: 76561198123456789,
			expect:  true,
		},
		{
			name:    "nil ip",
			ip:      nil,
			steamID: 76561198987654321,
			expect:  true,
		},
		{
			name:    "zero steamid",
			ip:      net.ParseIP("172.16.0.1"),
			steamID: 0,
			expect:  true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			limiter := NewRateLimiter(10.0)

			got := limiter.AllowPacket(tt.ip, tt.steamID)
			if got != tt.expect {
				t.Errorf("AllowPacket() = %v, want %v", got, tt.expect)
			}
		})
	}
}

func TestAllowPacketIPLimitExceeded(t *testing.T) {
	limiter := NewRateLimiter(3.0) // 3 pps

	ip := net.ParseIP("192.168.1.1")

	// Consume IP budget
	for i := 0; i < 3; i++ {
		if !limiter.AllowPacket(ip, 0) {
			t.Errorf("AllowPacket() packet %d should be allowed", i+1)
		}
	}

	// Next packet should fail due to IP limit
	if limiter.AllowPacket(ip, 0) {
		t.Error("AllowPacket() should fail when IP limit exceeded")
	}
}

func TestAllowPacketSteamIDLimitExceeded(t *testing.T) {
	limiter := NewRateLimiter(3.0) // 3 pps

	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)

	// Consume SteamID budget (not IP, so use different IP)
	for i := 0; i < 3; i++ {
		testIP := net.ParseIP(fmt.Sprintf("192.168.1.%d", i+10))
		if !limiter.AllowPacket(testIP, steamID) {
			t.Errorf("AllowPacket() packet %d should be allowed", i+1)
		}
	}

	// Next packet with same SteamID should fail
	if limiter.AllowPacket(ip, steamID) {
		t.Error("AllowPacket() should fail when SteamID limit exceeded")
	}
}

func TestAllowPacketBothIPAndSteamIDLimitExceeded(t *testing.T) {
	limiter := NewRateLimiter(2.0) // 2 pps per key

	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)

	// Consume IP budget
	for i := 0; i < 2; i++ {
		if !limiter.AllowPacket(ip, 0) {
			t.Errorf("IP budget packet %d should be allowed", i+1)
		}
	}

	// Try to use same IP with SteamID
	// Should fail due to IP limit being exceeded
	if limiter.AllowPacket(ip, steamID) {
		t.Error("AllowPacket() should fail when both IP and SteamID limits exceeded")
	}
}

func TestAllowPacketConsumesFromBothBuckets(t *testing.T) {
	limiter := NewRateLimiter(2.0) // 2 pps per key

	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)

	// First packet should succeed (consume from both IP and SteamID buckets)
	if !limiter.AllowPacket(ip, steamID) {
		t.Error("AllowPacket() first packet should be allowed")
	}

	// Second packet with same IP and SteamID should succeed
	if !limiter.AllowPacket(ip, steamID) {
		t.Error("AllowPacket() second packet should be allowed")
	}

	// Third packet should fail (both buckets at limit)
	if limiter.AllowPacket(ip, steamID) {
		t.Error("AllowPacket() third packet should be dropped")
	}
}

func TestAllowPacketDisabledRateLimit(t *testing.T) {
	limiter := NewRateLimiter(0) // disabled

	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)

	// All packets should be allowed
	for i := 0; i < 100; i++ {
		if !limiter.AllowPacket(ip, steamID) {
			t.Errorf("AllowPacket() should allow all packets when rate limiting disabled, failed at packet %d", i+1)
		}
	}
}
