package rules

import (
	"net"
	"strconv"
	"testing"
	"time"
)

func TestAddRule(t *testing.T) {
	tests := []struct {
		name      string
		ruleCount int
		shouldErr bool
	}{
		{
			name:      "add single rule",
			ruleCount: 1,
			shouldErr: false,
		},
		{
			name:      "add max rules",
			ruleCount: MaxRules,
			shouldErr: false,
		},
		{
			name:      "exceed max rules",
			ruleCount: MaxRules + 1,
			shouldErr: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			engine := NewEngine()

			var lastErr error
			for i := 0; i < tt.ruleCount; i++ {
				rule := Rule{
					ID:     "rule_" + strconv.Itoa(i),
					Name:   "test rule",
					Action: ActionLog,
				}
				lastErr = engine.Add(rule)
			}

			if (lastErr != nil) != tt.shouldErr {
				t.Errorf("Add() error = %v, want error = %v", lastErr != nil, tt.shouldErr)
			}
		})
	}
}

func TestAddDuplicateID(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:     "rule_1",
		Name:   "test",
		Action: ActionLog,
	}

	// Add first rule
	err := engine.Add(rule)
	if err != nil {
		t.Fatalf("First Add() should succeed, got error: %v", err)
	}

	// Try to add with same ID
	err = engine.Add(rule)
	if err == nil {
		t.Error("Add() with duplicate ID should return error")
	}
}

func TestRemoveRule(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:     "rule_1",
		Name:   "test",
		Action: ActionLog,
	}

	engine.Add(rule)

	if engine.count.Load() != 1 {
		t.Errorf("Count after Add = %d, want 1", engine.count.Load())
	}

	engine.Remove(rule.ID)

	if engine.count.Load() != 0 {
		t.Errorf("Count after Remove = %d, want 0", engine.count.Load())
	}

	// Verify rule is gone
	_, exists := engine.Get(rule.ID)
	if exists {
		t.Error("Remove() should delete the rule")
	}
}

func TestUpdateRule(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:     "rule_1",
		Name:   "original",
		Action: ActionLog,
	}

	engine.Add(rule)

	// Update the rule
	updatedRule := Rule{
		ID:     "rule_1",
		Name:   "updated",
		Action: ActionDrop,
	}

	err := engine.Update(updatedRule)
	if err != nil {
		t.Fatalf("Update() should succeed, got error: %v", err)
	}

	// Verify update
	retrieved, exists := engine.Get("rule_1")
	if !exists {
		t.Error("Update() rule should still exist")
	}

	if retrieved.Name != "updated" {
		t.Errorf("Update() Name = %s, want 'updated'", retrieved.Name)
	}

	if retrieved.Action != ActionDrop {
		t.Errorf("Update() Action = %v, want ActionDrop", retrieved.Action)
	}
}

func TestUpdateNonExistentRule(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:     "nonexistent",
		Name:   "test",
		Action: ActionLog,
	}

	err := engine.Update(rule)
	if err == nil {
		t.Error("Update() non-existent rule should return error")
	}
}

func TestGetRule(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:     "rule_1",
		Name:   "test",
		Action: ActionLog,
	}

	engine.Add(rule)

	retrieved, exists := engine.Get("rule_1")
	if !exists {
		t.Error("Get() should find the rule")
	}

	if retrieved.ID != rule.ID || retrieved.Name != rule.Name {
		t.Error("Get() returned incorrect rule")
	}
}

func TestGetNonExistent(t *testing.T) {
	engine := NewEngine()

	_, exists := engine.Get("nonexistent")
	if exists {
		t.Error("Get() non-existent rule should return false")
	}
}

func TestListRules(t *testing.T) {
	engine := NewEngine()

	rules := []Rule{
		{ID: "rule_1", Name: "test1", Priority: 2, Action: ActionLog},
		{ID: "rule_2", Name: "test2", Priority: 1, Action: ActionLog},
		{ID: "rule_3", Name: "test3", Priority: 3, Action: ActionLog},
	}

	for _, rule := range rules {
		engine.Add(rule)
	}

	list := engine.List()

	if len(list) != len(rules) {
		t.Errorf("List() returned %d rules, want %d", len(list), len(rules))
	}

	// Verify sorted by Priority ascending
	for i := 0; i < len(list)-1; i++ {
		if list[i].Priority > list[i+1].Priority {
			t.Error("List() should be sorted by Priority ascending")
		}
	}
}

func TestListExcludesExpiredRules(t *testing.T) {
	engine := NewEngine()

	rule1 := Rule{
		ID:       "rule_1",
		Name:     "permanent",
		Action:   ActionLog,
		TTLSec:   0, // permanent
	}

	rule2 := Rule{
		ID:       "rule_2",
		Name:     "expired",
		Action:   ActionLog,
		TTLSec:   1, // 1 second TTL
	}

	engine.Add(rule1)
	engine.Add(rule2)

	// Wait for rule2 to expire
	time.Sleep(1100 * time.Millisecond)

	list := engine.List()

	// Only rule1 should be in list
	if len(list) != 1 {
		t.Errorf("List() should exclude expired rules, got %d rules", len(list))
	}

	if list[0].ID != "rule_1" {
		t.Error("List() should only contain non-expired rule")
	}
}

func TestRuleExpired(t *testing.T) {
	tests := []struct {
		name      string
		ttlSec    int64
		waitTime  time.Duration
		shouldExp bool
	}{
		{
			name:      "permanent rule not expired",
			ttlSec:    0,
			waitTime:  100 * time.Millisecond,
			shouldExp: false,
		},
		{
			name:      "rule with ttl not expired",
			ttlSec:    10,
			waitTime:  100 * time.Millisecond,
			shouldExp: false,
		},
		{
			name:      "rule with ttl expired",
			ttlSec:    1,
			waitTime:  1100 * time.Millisecond,
			shouldExp: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			rule := Rule{
				ID:        "test",
				TTLSec:    tt.ttlSec,
				CreatedAt: time.Now(),
			}

			time.Sleep(tt.waitTime)
			got := rule.Expired()

			if got != tt.shouldExp {
				t.Errorf("Rule.Expired() = %v, want %v", got, tt.shouldExp)
			}
		})
	}
}

func TestEvaluateMatch(t *testing.T) {
	engine := NewEngine()

	// Add a rule that matches on specific port
	rule := Rule{
		ID:       "rule_1",
		Name:     "port rule",
		Action:   ActionDrop,
		Priority: 1,
		Match: Match{
			Port: 7777,
		},
	}

	engine.Add(rule)

	ip := net.ParseIP("192.168.1.1")
	matched, action, ruleID := engine.Evaluate(ip, 0, []byte{}, 7777, 0, 0, 0, 0)

	if !matched {
		t.Error("Evaluate() should match on port")
	}

	if action != ActionDrop {
		t.Errorf("Evaluate() action = %v, want ActionDrop", action)
	}

	if ruleID != "rule_1" {
		t.Errorf("Evaluate() ruleID = %s, want 'rule_1'", ruleID)
	}
}

func TestEvaluateNoMatch(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:       "rule_1",
		Name:     "port rule",
		Action:   ActionDrop,
		Priority: 1,
		Match: Match{
			Port: 7777,
		},
	}

	engine.Add(rule)

	ip := net.ParseIP("192.168.1.1")
	matched, action, ruleID := engine.Evaluate(ip, 0, []byte{}, 8888, 0, 0, 0, 0)

	if matched {
		t.Error("Evaluate() should not match on different port")
	}

	if action != "" {
		t.Errorf("Evaluate() action = %v, want empty", action)
	}

	if ruleID != "" {
		t.Errorf("Evaluate() ruleID = %v, want empty", ruleID)
	}
}

func TestEvaluatePriority(t *testing.T) {
	engine := NewEngine()

	// Add two matching rules with different priorities
	rule1 := Rule{
		ID:       "rule_1",
		Action:   ActionAlert,
		Priority: 2,
		Match:    Match{Port: 7777},
	}

	rule2 := Rule{
		ID:       "rule_2",
		Action:   ActionDrop,
		Priority: 1,
	}

	engine.Add(rule1)
	engine.Add(rule2)

	ip := net.ParseIP("192.168.1.1")
	_, action, ruleID := engine.Evaluate(ip, 0, []byte{}, 7777, 0, 0, 0, 0)

	// rule2 has priority 1 (lower), should match first
	if ruleID != "rule_2" {
		t.Errorf("Evaluate() should use lowest priority rule, got %s", ruleID)
	}

	if action != ActionDrop {
		t.Errorf("Evaluate() action = %v, want ActionDrop", action)
	}
}

func TestEvaluateSrcCIDR(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:       "rule_1",
		Action:   ActionDrop,
		Priority: 1,
		Match: Match{
			SrcCIDR: "192.168.1.0/24",
		},
	}

	engine.Add(rule)

	// Within CIDR
	ip1 := net.ParseIP("192.168.1.100")
	matched, _, _ := engine.Evaluate(ip1, 0, []byte{}, 0, 0, 0, 0, 0)
	if !matched {
		t.Error("Evaluate() should match within CIDR")
	}

	// Outside CIDR
	ip2 := net.ParseIP("192.168.2.100")
	matched, _, _ = engine.Evaluate(ip2, 0, []byte{}, 0, 0, 0, 0, 0)
	if matched {
		t.Error("Evaluate() should not match outside CIDR")
	}
}

func TestEvaluatePPSRange(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:       "rule_1",
		Action:   ActionDrop,
		Priority: 1,
		Match: Match{
			PPSMin: 10,
			PPSMax: 100,
		},
	}

	engine.Add(rule)

	ip := net.ParseIP("192.168.1.1")

	// Within range
	matched, _, _ := engine.Evaluate(ip, 0, []byte{}, 0, 50, 0, 0, 0)
	if !matched {
		t.Error("Evaluate() should match within PPS range")
	}

	// Below range
	matched, _, _ = engine.Evaluate(ip, 0, []byte{}, 0, 5, 0, 0, 0)
	if matched {
		t.Error("Evaluate() should not match below PPS range")
	}

	// Above range
	matched, _, _ = engine.Evaluate(ip, 0, []byte{}, 0, 150, 0, 0, 0)
	if matched {
		t.Error("Evaluate() should not match above PPS range")
	}
}

func TestEvaluatePacketSize(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:       "rule_1",
		Action:   ActionDrop,
		Priority: 1,
		Match: Match{
			PacketSizeMin: 50,
			PacketSizeMax: 200,
		},
	}

	engine.Add(rule)

	ip := net.ParseIP("192.168.1.1")

	// Within range
	matched, _, _ := engine.Evaluate(ip, 0, []byte{}, 0, 0, 0, 100, 0)
	if !matched {
		t.Error("Evaluate() should match within packet size range")
	}

	// Below range
	matched, _, _ = engine.Evaluate(ip, 0, []byte{}, 0, 0, 0, 25, 0)
	if matched {
		t.Error("Evaluate() should not match below packet size range")
	}

	// Above range
	matched, _, _ = engine.Evaluate(ip, 0, []byte{}, 0, 0, 0, 300, 0)
	if matched {
		t.Error("Evaluate() should not match above packet size range")
	}
}

func TestEvaluatePayloadHex(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:       "rule_1",
		Action:   ActionDrop,
		Priority: 1,
		Match: Match{
			PayloadHex: "deadbeef",
		},
	}

	engine.Add(rule)

	ip := net.ParseIP("192.168.1.1")

	// Payload contains the hex
	payload := []byte{0xDE, 0xAD, 0xBE, 0xEF, 0xFF}
	matched, _, _ := engine.Evaluate(ip, 0, payload, 0, 0, 0, 0, 0)
	if !matched {
		t.Error("Evaluate() should match payload hex")
	}

	// Payload doesn't contain the hex
	payload2 := []byte{0xFF, 0xFF, 0xFF, 0xFF}
	matched, _, _ = engine.Evaluate(ip, 0, payload2, 0, 0, 0, 0, 0)
	if matched {
		t.Error("Evaluate() should not match without payload hex")
	}
}

func TestEvaluateSteamID(t *testing.T) {
	engine := NewEngine()

	steamIDStr := "76561198123456789"
	rule := Rule{
		ID:       "rule_1",
		Action:   ActionDrop,
		Priority: 1,
		Match: Match{
			SteamID: steamIDStr,
		},
	}

	engine.Add(rule)

	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)

	// Matching SteamID
	matched, _, _ := engine.Evaluate(ip, steamID, []byte{}, 0, 0, 0, 0, 0)
	if !matched {
		t.Error("Evaluate() should match SteamID")
	}

	// Different SteamID
	matched, _, _ = engine.Evaluate(ip, 999999999, []byte{}, 0, 0, 0, 0, 0)
	if matched {
		t.Error("Evaluate() should not match different SteamID")
	}
}

func TestEvaluateSkipsExpiredRules(t *testing.T) {
	engine := NewEngine()

	// Add expired rule
	rule1 := Rule{
		ID:       "rule_1",
		Action:   ActionDrop,
		Priority: 1,
		TTLSec:   1,
		Match:    Match{Port: 7777},
	}

	// Add valid rule
	rule2 := Rule{
		ID:       "rule_2",
		Action:   ActionAlert,
		Priority: 2,
		TTLSec:   0,
		Match:    Match{Port: 7777},
	}

	engine.Add(rule1)
	engine.Add(rule2)

	// Wait for rule1 to expire
	time.Sleep(1100 * time.Millisecond)

	ip := net.ParseIP("192.168.1.1")
	_, action, ruleID := engine.Evaluate(ip, 0, []byte{}, 7777, 0, 0, 0, 0)

	if ruleID != "rule_2" {
		t.Errorf("Evaluate() should skip expired rule, got %s", ruleID)
	}

	if action != ActionAlert {
		t.Errorf("Evaluate() action = %v, want ActionAlert", action)
	}
}

func TestEvaluateEmptyEngine(t *testing.T) {
	engine := NewEngine()

	ip := net.ParseIP("192.168.1.1")
	matched, action, ruleID := engine.Evaluate(ip, 0, []byte{}, 0, 0, 0, 0, 0)

	if matched {
		t.Error("Evaluate() empty engine should not match")
	}

	if action != "" || ruleID != "" {
		t.Error("Evaluate() empty engine should return empty values")
	}
}

func TestEvaluateTimingCV(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:       "rule_1",
		Action:   ActionDrop,
		Priority: 1,
		Match: Match{
			TimingCVMax: 0.5,
		},
	}

	engine.Add(rule)

	ip := net.ParseIP("192.168.1.1")

	// Within threshold (not anomalous)
	matched, _, _ := engine.Evaluate(ip, 0, []byte{}, 0, 0, 0, 0, 0.3)
	if !matched {
		t.Error("Evaluate() should match when timing CV below threshold")
	}

	// Above threshold (anomalous)
	matched, _, _ = engine.Evaluate(ip, 0, []byte{}, 0, 0, 0, 0, 0.7)
	if matched {
		t.Error("Evaluate() should not match when timing CV above threshold")
	}
}

func TestEvaluateMultipleConditions(t *testing.T) {
	engine := NewEngine()

	rule := Rule{
		ID:       "rule_1",
		Action:   ActionDrop,
		Priority: 1,
		Match: Match{
			Port:         7777,
			PPSMin:       10,
			PPSMax:       100,
			PacketSizeMin: 50,
		},
	}

	engine.Add(rule)

	ip := net.ParseIP("192.168.1.1")

	// All conditions met
	matched, _, _ := engine.Evaluate(ip, 0, []byte{}, 7777, 50, 0, 100, 0)
	if !matched {
		t.Error("Evaluate() should match when all conditions met")
	}

	// Port doesn't match
	matched, _, _ = engine.Evaluate(ip, 0, []byte{}, 8888, 50, 0, 100, 0)
	if matched {
		t.Error("Evaluate() should not match when port doesn't match")
	}

	// PPS too low
	matched, _, _ = engine.Evaluate(ip, 0, []byte{}, 7777, 5, 0, 100, 0)
	if matched {
		t.Error("Evaluate() should not match when PPS too low")
	}

	// Packet size too small
	matched, _, _ = engine.Evaluate(ip, 0, []byte{}, 7777, 50, 0, 30, 0)
	if matched {
		t.Error("Evaluate() should not match when packet size too small")
	}
}
