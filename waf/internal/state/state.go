package state

import (
	"net"
	"sync"
	"sync/atomic"
	"time"
)

// Entry in a time-limited set. Zero Time = permanent.
type Entry struct {
	Expiry time.Time
	Reason string
}

// ThrottleEntry tracks throttling state for an IP.
type ThrottleEntry struct {
	Factor float64
	Until  time.Time
}

// Store holds all runtime WAF state. Create with New().
type Store struct {
	blockedIPs      sync.Map // key: string(ip), val: Entry
	blockedSteamIDs sync.Map // key: uint64,      val: Entry
	allowedIPs      sync.Map // key: string(ip),  val: struct{}
	priorityIDs     sync.Map // key: uint64,       val: struct{}   (admin/owner SteamIDs)
	throttledIPs    sync.Map // key: string(ip),  val: ThrottleEntry
	ipToSteam       sync.Map // key: string(ip),  val: uint64      (IP to SteamID association)
	ruleCount       atomic.Int64
}

// New creates a new empty Store.
func New() *Store {
	return &Store{}
}

// BlockIP adds ip to the blocked set. Zero duration means permanent.
// If ip is marked as priority, this is a no-op.
func (s *Store) BlockIP(ip net.IP, duration time.Duration, reason string) {
	if ip == nil {
		return
	}
	ipStr := ip.String()

	// Check if this IP's SteamID is priority; if so, do nothing
	if val, ok := s.ipToSteam.Load(ipStr); ok {
		if steamID, ok := val.(uint64); ok {
			if s.IsPriority(steamID) {
				return
			}
		}
	}

	expiry := time.Time{}
	if duration > 0 {
		expiry = time.Now().Add(duration)
	}

	s.blockedIPs.Store(ipStr, Entry{Expiry: expiry, Reason: reason})
}

// UnblockIP removes ip from the blocked set.
func (s *Store) UnblockIP(ip net.IP) {
	if ip == nil {
		return
	}
	s.blockedIPs.Delete(ip.String())
}

// IsIPBlocked returns true if ip is in the blocked set and not expired.
// Stale entries are deleted during the check.
func (s *Store) IsIPBlocked(ip net.IP) bool {
	if ip == nil {
		return false
	}

	val, ok := s.blockedIPs.Load(ip.String())
	if !ok {
		return false
	}

	entry, ok := val.(Entry)
	if !ok {
		return false
	}

	// Check if entry is still valid (zero expiry = permanent, or not yet expired)
	if !entry.Expiry.IsZero() && time.Now().After(entry.Expiry) {
		// Entry has expired; delete it
		s.blockedIPs.Delete(ip.String())
		return false
	}

	return true
}

// BlockSteamID adds steamID to the blocked set. Zero duration means permanent.
// If steamID is marked as priority, this is a no-op.
// Zero steamID is never blocked (invalid steamID).
func (s *Store) BlockSteamID(id uint64, duration time.Duration, reason string) {
	if id == 0 {
		return
	}

	// Check if this SteamID is priority; if so, do nothing
	if s.IsPriority(id) {
		return
	}

	expiry := time.Time{}
	if duration > 0 {
		expiry = time.Now().Add(duration)
	}

	s.blockedSteamIDs.Store(id, Entry{Expiry: expiry, Reason: reason})
}

// UnblockSteamID removes steamID from the blocked set.
func (s *Store) UnblockSteamID(id uint64) {
	s.blockedSteamIDs.Delete(id)
}

// IsSteamIDBlocked returns true if steamID is in the blocked set and not expired.
// Stale entries are deleted during the check.
// Zero steamID is never blocked (invalid steamID).
func (s *Store) IsSteamIDBlocked(id uint64) bool {
	if id == 0 {
		return false
	}

	val, ok := s.blockedSteamIDs.Load(id)
	if !ok {
		return false
	}

	entry, ok := val.(Entry)
	if !ok {
		return false
	}

	// Check if entry is still valid
	if !entry.Expiry.IsZero() && time.Now().After(entry.Expiry) {
		// Entry has expired; delete it
		s.blockedSteamIDs.Delete(id)
		return false
	}

	return true
}

// AllowIP adds ip to the allowed set (whitelist).
func (s *Store) AllowIP(ip net.IP) {
	if ip == nil {
		return
	}
	s.allowedIPs.Store(ip.String(), struct{}{})
}

// DisallowIP removes ip from the allowed set.
func (s *Store) DisallowIP(ip net.IP) {
	if ip == nil {
		return
	}
	s.allowedIPs.Delete(ip.String())
}

// IsIPAllowed returns true if ip is in the allowed set.
func (s *Store) IsIPAllowed(ip net.IP) bool {
	if ip == nil {
		return false
	}
	_, ok := s.allowedIPs.Load(ip.String())
	return ok
}

// SetPriority marks steamID as admin/owner (priority).
func (s *Store) SetPriority(id uint64) {
	s.priorityIDs.Store(id, struct{}{})
}

// UnsetPriority removes steamID from priority set.
func (s *Store) UnsetPriority(id uint64) {
	s.priorityIDs.Delete(id)
}

// IsPriority returns true if steamID is marked as priority.
func (s *Store) IsPriority(id uint64) bool {
	_, ok := s.priorityIDs.Load(id)
	return ok
}

// IsPriorityBlocked always returns false. Priority IDs cannot be blocked.
func (s *Store) IsPriorityBlocked(id uint64) bool {
	return false
}

// ThrottleIP adds ip to the throttle set with the given factor and duration.
func (s *Store) ThrottleIP(ip net.IP, factor float64, duration time.Duration) {
	if ip == nil || duration <= 0 {
		return
	}
	s.throttledIPs.Store(ip.String(), ThrottleEntry{Factor: factor, Until: time.Now().Add(duration)})
}

// GetThrottle returns the throttle factor for ip if it is active.
// Returns (0, false) if ip is not throttled or entry has expired.
func (s *Store) GetThrottle(ip net.IP) (float64, bool) {
	if ip == nil {
		return 0, false
	}

	val, ok := s.throttledIPs.Load(ip.String())
	if !ok {
		return 0, false
	}

	entry, ok := val.(ThrottleEntry)
	if !ok {
		return 0, false
	}

	// Check if entry has expired
	if time.Now().After(entry.Until) {
		s.throttledIPs.Delete(ip.String())
		return 0, false
	}

	return entry.Factor, true
}

// BlockedIPsCount returns the count of blocked IPs (not cleaned up for expiry).
func (s *Store) BlockedIPsCount() int {
	count := 0
	s.blockedIPs.Range(func(key, val interface{}) bool {
		count++
		return true
	})
	return count
}

// BlockedSteamIDsCount returns the count of blocked SteamIDs (not cleaned up for expiry).
func (s *Store) BlockedSteamIDsCount() int {
	count := 0
	s.blockedSteamIDs.Range(func(key, val interface{}) bool {
		count++
		return true
	})
	return count
}
