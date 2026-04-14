package rules

import (
	"encoding/hex"
	"fmt"
	"net"
	"sort"
	"strconv"
	"sync"
	"sync/atomic"
	"time"
)

// Action defines the WAF action to take on a matched rule.
type Action string

const (
	ActionLog      Action = "LOG"
	ActionAlert    Action = "ALERT"
	ActionThrottle Action = "THROTTLE"
	ActionDrop     Action = "DROP"
	ActionBlock    Action = "BLOCK"
)

// IsMonitor returns true for LOG and ALERT (traffic flows unaffected).
func (a Action) IsMonitor() bool {
	return a == ActionLog || a == ActionAlert
}

// IsBlock returns true for THROTTLE, DROP, BLOCK.
func (a Action) IsBlock() bool {
	return a == ActionThrottle || a == ActionDrop || a == ActionBlock
}

// Match defines the conditions for a rule to match a packet.
type Match struct {
	SrcCIDR        string  // e.g. "0.0.0.0/0"; empty = match all
	PPSMin         float64 // packets per second; 0 = unconstrained
	PPSMax         float64
	BytesPerSecMin float64
	BytesPerSecMax float64
	PacketSizeMin  int
	PacketSizeMax  int
	PayloadHex     string  // hex substring match against raw payload; empty = skip
	TimingCVMax    float64 // coefficient of variation; 0 = disabled
	SteamID        string  // exact Steam64 ID as decimal string; empty = skip
	Port           int     // 0 = all ports
}

// Rule represents a single WAF rule.
type Rule struct {
	ID        string
	Name      string
	Match     Match
	Action    Action
	Priority  int
	TTLSec    int64 // 0 = permanent
	CreatedAt time.Time
}

// Expired returns true if the rule has a TTL that has passed.
func (r Rule) Expired() bool {
	if r.TTLSec <= 0 {
		return false
	}
	return time.Since(r.CreatedAt) > time.Duration(r.TTLSec)*time.Second
}

const MaxRules = 200

// Engine is the WAF rules engine. Thread-safe.
type Engine struct {
	rules     sync.Map   // key: string(ID) → Rule
	count     atomic.Int64
	sortedMu  sync.RWMutex
	sorted    []Rule // priority-sorted snapshot, rebuilt on mutations
}

// NewEngine creates a new rules engine.
func NewEngine() *Engine {
	return &Engine{
		sorted: make([]Rule, 0),
	}
}

// Add adds a new rule to the engine. Returns error if count >= MaxRules or ID already exists.
func (e *Engine) Add(rule Rule) error {
	if e.count.Load() >= MaxRules {
		return fmt.Errorf("max rules (%d) reached", MaxRules)
	}

	if _, exists := e.rules.Load(rule.ID); exists {
		return fmt.Errorf("rule ID %q already exists", rule.ID)
	}

	rule.CreatedAt = time.Now()
	e.rules.Store(rule.ID, rule)
	e.count.Add(1)
	e.rebuildSorted()

	return nil
}

// Update replaces an existing rule by ID. Returns error if rule does not exist.
func (e *Engine) Update(rule Rule) error {
	if _, exists := e.rules.Load(rule.ID); !exists {
		return fmt.Errorf("rule ID %q not found", rule.ID)
	}

	// Preserve original CreatedAt
	if existing, ok := e.rules.Load(rule.ID); ok {
		if existingRule, ok := existing.(Rule); ok {
			rule.CreatedAt = existingRule.CreatedAt
		}
	}

	e.rules.Store(rule.ID, rule)
	e.rebuildSorted()

	return nil
}

// Remove deletes a rule by ID.
func (e *Engine) Remove(id string) {
	if _, exists := e.rules.Load(id); exists {
		e.rules.Delete(id)
		e.count.Add(-1)
		e.rebuildSorted()
	}
}

// Get retrieves a rule by ID.
func (e *Engine) Get(id string) (Rule, bool) {
	val, exists := e.rules.Load(id)
	if !exists {
		return Rule{}, false
	}
	rule, ok := val.(Rule)
	return rule, ok
}

// List returns all non-expired rules sorted by Priority ascending.
func (e *Engine) List() []Rule {
	e.sortedMu.RLock()
	defer e.sortedMu.RUnlock()

	result := make([]Rule, 0, len(e.sorted))
	for _, rule := range e.sorted {
		if !rule.Expired() {
			result = append(result, rule)
		}
	}
	return result
}

// Evaluate evaluates a packet against all rules and returns the first match.
// Thread-safe via RLock on sortedMu.
func (e *Engine) Evaluate(
	ip net.IP,
	steamID uint64,
	payload []byte,
	port int,
	pps float64,
	bytesPerSec float64,
	packetSize int,
	timingCV float64,
) (matched bool, action Action, ruleID string) {
	e.sortedMu.RLock()
	sorted := e.sorted
	e.sortedMu.RUnlock()

	var expiredIDs []string

	for _, rule := range sorted {
		if rule.Expired() {
			expiredIDs = append(expiredIDs, rule.ID)
			continue
		}

		// Match logic
		if !e.matchRule(rule, ip, steamID, payload, port, pps, bytesPerSec, packetSize, timingCV) {
			continue
		}

		// First match wins
		return true, rule.Action, rule.ID
	}

	// Clean up expired rules in background
	if len(expiredIDs) > 0 {
		go func() {
			for _, id := range expiredIDs {
				e.Remove(id)
			}
		}()
	}

	return false, "", ""
}

// matchRule checks if a packet matches a rule's conditions.
func (e *Engine) matchRule(
	rule Rule,
	ip net.IP,
	steamID uint64,
	payload []byte,
	port int,
	pps float64,
	bytesPerSec float64,
	packetSize int,
	timingCV float64,
) bool {
	m := rule.Match

	// Port check
	if m.Port != 0 && m.Port != port {
		return false
	}

	// CIDR check
	if m.SrcCIDR != "" && m.SrcCIDR != "0.0.0.0/0" {
		_, cidrNet, err := net.ParseCIDR(m.SrcCIDR)
		if err == nil && !cidrNet.Contains(ip) {
			return false
		}
	}

	// PPS range check
	if m.PPSMin > 0 && pps < m.PPSMin {
		return false
	}
	if m.PPSMax > 0 && pps > m.PPSMax {
		return false
	}

	// BytesPerSec range check
	if m.BytesPerSecMin > 0 && bytesPerSec < m.BytesPerSecMin {
		return false
	}
	if m.BytesPerSecMax > 0 && bytesPerSec > m.BytesPerSecMax {
		return false
	}

	// PacketSize range check
	if m.PacketSizeMin > 0 && packetSize < m.PacketSizeMin {
		return false
	}
	if m.PacketSizeMax > 0 && packetSize > m.PacketSizeMax {
		return false
	}

	// Payload hex substring check
	if m.PayloadHex != "" {
		matchBytes, err := hex.DecodeString(m.PayloadHex)
		if err == nil && !e.bytesContains(payload, matchBytes) {
			return false
		}
	}

	// Timing CV check (higher CV = more variance = anomalous)
	if m.TimingCVMax > 0 && timingCV > m.TimingCVMax {
		return false
	}

	// SteamID check
	if m.SteamID != "" {
		ruleID, err := strconv.ParseUint(m.SteamID, 10, 64)
		if err != nil || ruleID != steamID {
			return false
		}
	}

	return true
}

// bytesContains returns true if haystack contains needle as a substring.
func (e *Engine) bytesContains(haystack, needle []byte) bool {
	if len(needle) == 0 {
		return true
	}
	if len(needle) > len(haystack) {
		return false
	}
	for i := 0; i <= len(haystack)-len(needle); i++ {
		if string(haystack[i:i+len(needle)]) == string(needle) {
			return true
		}
	}
	return false
}

// rebuildSorted rebuilds the sorted rule snapshot. Must be called while holding relevant locks.
func (e *Engine) rebuildSorted() {
	e.sortedMu.Lock()
	defer e.sortedMu.Unlock()

	rules := make([]Rule, 0)
	e.rules.Range(func(key, value interface{}) bool {
		if rule, ok := value.(Rule); ok {
			rules = append(rules, rule)
		}
		return true
	})

	sort.Slice(rules, func(i, j int) bool {
		return rules[i].Priority < rules[j].Priority
	})

	e.sorted = rules
}
