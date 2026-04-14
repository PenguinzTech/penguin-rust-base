package detect

import (
	"encoding/binary"
	"net"
	"sync"
)

// Steam64 ID range limits
const (
	steam64Min = uint64(0x0110000100000000)
	steam64Max = uint64(0x01100001FFFFFFFF)
)

// Mapper tracks IP↔SteamID associations.
type Mapper struct {
	steamToIPs sync.Map // uint64 → []string  (IP strings)
	ipToSteam  sync.Map // string  → uint64
}

// NewMapper creates a new Mapper.
func NewMapper() *Mapper {
	return &Mapper{}
}

// Extract scans payload for a Steam64 ID. Returns (0, false) if none found.
// Scans every 8-byte aligned and unaligned window looking for values in the Steam64 range.
func (m *Mapper) Extract(payload []byte) (steamID uint64, found bool) {
	if len(payload) < 8 {
		return 0, false
	}

	// Scan all 8-byte windows (aligned and unaligned)
	for i := 0; i <= len(payload)-8; i++ {
		// Try little-endian
		le := binary.LittleEndian.Uint64(payload[i : i+8])
		if le >= steam64Min && le <= steam64Max {
			return le, true
		}

		// Try big-endian
		be := binary.BigEndian.Uint64(payload[i : i+8])
		if be >= steam64Min && be <= steam64Max {
			return be, true
		}
	}

	return 0, false
}

// Record associates ip with steamID. Thread-safe.
func (m *Mapper) Record(ip net.IP, steamID uint64) {
	if ip == nil || steamID == 0 {
		return
	}

	ipStr := ip.String()

	// Store IP → SteamID mapping
	m.ipToSteam.Store(ipStr, steamID)

	// Store SteamID → IPs mapping
	val, ok := m.steamToIPs.Load(steamID)
	if !ok {
		// First IP for this SteamID
		m.steamToIPs.Store(steamID, []string{ipStr})
		return
	}

	ips, ok := val.([]string)
	if !ok {
		ips = []string{}
	}

	// Check if IP already exists in the slice
	for _, existing := range ips {
		if existing == ipStr {
			return // Already recorded
		}
	}

	// Append new IP
	ips = append(ips, ipStr)
	m.steamToIPs.Store(steamID, ips)
}

// IPsForSteam returns all IPs ever seen for a SteamID.
func (m *Mapper) IPsForSteam(steamID uint64) []net.IP {
	val, ok := m.steamToIPs.Load(steamID)
	if !ok {
		return []net.IP{}
	}

	ips, ok := val.([]string)
	if !ok {
		return []net.IP{}
	}

	result := make([]net.IP, 0, len(ips))
	for _, ipStr := range ips {
		if parsed := net.ParseIP(ipStr); parsed != nil {
			result = append(result, parsed)
		}
	}

	return result
}

// SteamForIP returns the SteamID associated with ip, if known.
func (m *Mapper) SteamForIP(ip net.IP) (uint64, bool) {
	if ip == nil {
		return 0, false
	}

	val, ok := m.ipToSteam.Load(ip.String())
	if !ok {
		return 0, false
	}

	steamID, ok := val.(uint64)
	return steamID, ok
}

// Count returns the total number of tracked SteamID→IP mappings.
func (m *Mapper) Count() int {
	count := 0
	m.steamToIPs.Range(func(key, val interface{}) bool {
		count++
		return true
	})
	return count
}
