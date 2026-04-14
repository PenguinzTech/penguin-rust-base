package api

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"net/http"
	"strconv"
	"sync"
	"time"

	"github.com/penguintechinc/penguin-rust-base/waf/internal/detect"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/rules"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/state"
)

type SuspectReport struct {
	SteamID   string    `json:"steam_id"`
	IP        string    `json:"ip"`
	Reason    string    `json:"reason"`
	Evidence  string    `json:"evidence"`
	Timestamp time.Time `json:"timestamp"`
}

type Server struct {
	store    *state.Store
	mapper   *detect.Mapper
	engine   *rules.Engine
	mux      *http.ServeMux
	suspects []SuspectReport
	suspMu   sync.Mutex
}

func New(store *state.Store, mapper *detect.Mapper, engine *rules.Engine) *Server {
	s := &Server{
		store:    store,
		mapper:   mapper,
		engine:   engine,
		mux:      http.NewServeMux(),
		suspects: make([]SuspectReport, 0, 500),
	}

	s.registerRoutes()
	return s
}

func (s *Server) registerRoutes() {
	s.mux.HandleFunc("GET /healthz", s.handleHealthz)

	// IP control
	s.mux.HandleFunc("POST /api/v1/block", s.handleBlockIP)
	s.mux.HandleFunc("DELETE /api/v1/block/{ip}", s.handleUnblockIP)
	s.mux.HandleFunc("POST /api/v1/throttle", s.handleThrottleIP)
	s.mux.HandleFunc("POST /api/v1/allowlist", s.handleAllowlist)
	s.mux.HandleFunc("POST /api/v1/report", s.handleReport)
	s.mux.HandleFunc("GET /api/v1/suspects", s.handleSuspects)
	s.mux.HandleFunc("GET /api/v1/stats/{ip}", s.handleStatsByIP)
	s.mux.HandleFunc("GET /api/v1/stats/steam/{steamId}", s.handleStatsBySteam)

	// Rule management
	s.mux.HandleFunc("POST /api/v1/rules", s.handleCreateRule)
	s.mux.HandleFunc("GET /api/v1/rules", s.handleListRules)
	s.mux.HandleFunc("GET /api/v1/rules/{id}", s.handleGetRule)
	s.mux.HandleFunc("PUT /api/v1/rules/{id}", s.handleUpdateRule)
	s.mux.HandleFunc("DELETE /api/v1/rules/{id}", s.handleDeleteRule)

	// Priority QoS
	s.mux.HandleFunc("POST /api/v1/priority", s.handleSetPriority)
	s.mux.HandleFunc("DELETE /api/v1/priority/{steamId}", s.handleUnsetPriority)
	s.mux.HandleFunc("GET /api/v1/priority", s.handleListPriority)
}

func (s *Server) Start(ctx context.Context, addr string) error {
	srv := &http.Server{
		Addr:    addr,
		Handler: s.mux,
	}

	errChan := make(chan error, 1)
	go func() {
		errChan <- srv.ListenAndServe()
	}()

	select {
	case <-ctx.Done():
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		return srv.Shutdown(shutdownCtx)
	case err := <-errChan:
		if err != nil && err != http.ErrServerClosed {
			return err
		}
		return nil
	}
}

// Handlers

func (s *Server) handleHealthz(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]string{"status": "ok"})
}

func (s *Server) handleBlockIP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var req struct {
		IP          string `json:"ip"`
		DurationSec int64  `json:"duration_sec"`
		Reason      string `json:"reason"`
	}

	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid json"}`, http.StatusBadRequest)
		return
	}

	ip := net.ParseIP(req.IP)
	if ip == nil {
		http.Error(w, `{"error":"invalid ip"}`, http.StatusBadRequest)
		return
	}

	// Check if IP maps to priority SteamID
	if steamID, ok := s.mapper.SteamForIP(ip); ok {
		if s.store.IsPriority(steamID) {
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusForbidden)
			json.NewEncoder(w).Encode(map[string]string{"error": "cannot block priority identity"})
			return
		}
	}

	var duration time.Duration
	if req.DurationSec > 0 {
		duration = time.Duration(req.DurationSec) * time.Second
	}

	s.store.BlockIP(ip, duration, req.Reason)

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]bool{"ok": true})
}

func (s *Server) handleUnblockIP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodDelete {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	ipStr := r.PathValue("ip")
	ip := net.ParseIP(ipStr)
	if ip == nil {
		http.Error(w, `{"error":"invalid ip"}`, http.StatusBadRequest)
		return
	}

	s.store.UnblockIP(ip)

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]bool{"ok": true})
}

func (s *Server) handleThrottleIP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var req struct {
		IP          string  `json:"ip"`
		Factor      float64 `json:"factor"`
		DurationSec int64   `json:"duration_sec"`
	}

	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid json"}`, http.StatusBadRequest)
		return
	}

	ip := net.ParseIP(req.IP)
	if ip == nil {
		http.Error(w, `{"error":"invalid ip"}`, http.StatusBadRequest)
		return
	}

	duration := time.Duration(req.DurationSec) * time.Second
	s.store.ThrottleIP(ip, req.Factor, duration)

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]bool{"ok": true})
}

func (s *Server) handleAllowlist(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var req struct {
		IP string `json:"ip"`
	}

	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid json"}`, http.StatusBadRequest)
		return
	}

	ip := net.ParseIP(req.IP)
	if ip == nil {
		http.Error(w, `{"error":"invalid ip"}`, http.StatusBadRequest)
		return
	}

	s.store.AllowIP(ip)

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]bool{"ok": true})
}

func (s *Server) handleReport(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var req SuspectReport

	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid json"}`, http.StatusBadRequest)
		return
	}

	req.Timestamp = time.Now()

	s.suspMu.Lock()
	if len(s.suspects) >= 500 {
		s.suspects = s.suspects[1:]
	}
	s.suspects = append(s.suspects, req)
	s.suspMu.Unlock()

	log.Printf("[WAF] Suspect report: steamid=%s ip=%s reason=%s", req.SteamID, req.IP, req.Reason)

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]bool{"ok": true})
}

func (s *Server) handleSuspects(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	s.suspMu.Lock()
	suspects := make([]SuspectReport, len(s.suspects))
	copy(suspects, s.suspects)
	s.suspMu.Unlock()

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]interface{}{"suspects": suspects})
}

func (s *Server) handleStatsByIP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	ipStr := r.PathValue("ip")
	ip := net.ParseIP(ipStr)
	if ip == nil {
		http.Error(w, `{"error":"invalid ip"}`, http.StatusBadRequest)
		return
	}

	steamID := ""
	if sid, ok := s.mapper.SteamForIP(ip); ok {
		steamID = fmt.Sprintf("%d", sid)
	}

	resp := map[string]interface{}{
		"ip":          ipStr,
		"steam_id":    steamID,
		"is_blocked":  s.store.IsIPBlocked(ip),
		"is_allowed":  s.store.IsIPAllowed(ip),
		"is_throttled": false, // Simplified: would need store to track throttled IPs
	}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(resp)
}

func (s *Server) handleStatsBySteam(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	steamIDStr := r.PathValue("steamId")
	steamID, err := strconv.ParseUint(steamIDStr, 10, 64)
	if err != nil {
		http.Error(w, `{"error":"invalid steam id"}`, http.StatusBadRequest)
		return
	}

	ips := s.mapper.IPsForSteam(steamID)
	ipStrs := make([]string, len(ips))
	for i, ip := range ips {
		ipStrs[i] = ip.String()
	}

	resp := map[string]interface{}{
		"steam_id":   steamIDStr,
		"ips":        ipStrs,
		"is_blocked": s.store.IsSteamIDBlocked(steamID),
	}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(resp)
}

func (s *Server) handleCreateRule(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	if s.engine.List() != nil && len(s.engine.List()) >= 200 {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusTooManyRequests)
		json.NewEncoder(w).Encode(map[string]string{"error": "rule limit reached"})
		return
	}

	var rule rules.Rule

	if err := json.NewDecoder(r.Body).Decode(&rule); err != nil {
		http.Error(w, `{"error":"invalid json"}`, http.StatusBadRequest)
		return
	}

	if rule.ID == "" {
		rule.ID = fmt.Sprintf("rule-%d", time.Now().UnixNano())
	}
	if rule.CreatedAt.IsZero() {
		rule.CreatedAt = time.Now()
	}

	if err := s.engine.Add(rule); err != nil {
		http.Error(w, fmt.Sprintf(`{"error":"%s"}`, err.Error()), http.StatusBadRequest)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusCreated)
	json.NewEncoder(w).Encode(map[string]string{"id": rule.ID})
}

func (s *Server) handleListRules(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]interface{}{"rules": s.engine.List()})
}

func (s *Server) handleGetRule(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	id := r.PathValue("id")
	rule, ok := s.engine.Get(id)
	if !ok {
		http.Error(w, `{"error":"not found"}`, http.StatusNotFound)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(rule)
}

func (s *Server) handleUpdateRule(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPut {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	id := r.PathValue("id")
	if id == "" {
		http.Error(w, `{"error":"invalid id"}`, http.StatusBadRequest)
		return
	}

	var rule rules.Rule

	if err := json.NewDecoder(r.Body).Decode(&rule); err != nil {
		http.Error(w, `{"error":"invalid json"}`, http.StatusBadRequest)
		return
	}

	rule.ID = id
	if err := s.engine.Update(rule); err != nil {
		http.Error(w, fmt.Sprintf(`{"error":"%s"}`, err.Error()), http.StatusBadRequest)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]bool{"ok": true})
}

func (s *Server) handleDeleteRule(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodDelete {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	id := r.PathValue("id")
	s.engine.Remove(id)

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]bool{"ok": true})
}

func (s *Server) handleSetPriority(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var req struct {
		SteamID string `json:"steam_id"`
	}

	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid json"}`, http.StatusBadRequest)
		return
	}

	steamID, err := strconv.ParseUint(req.SteamID, 10, 64)
	if err != nil {
		http.Error(w, `{"error":"invalid steam id"}`, http.StatusBadRequest)
		return
	}

	s.store.SetPriority(steamID)

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]bool{"ok": true})
}

func (s *Server) handleUnsetPriority(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodDelete {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	steamIDStr := r.PathValue("steamId")
	steamID, err := strconv.ParseUint(steamIDStr, 10, 64)
	if err != nil {
		http.Error(w, `{"error":"invalid steam id"}`, http.StatusBadRequest)
		return
	}

	s.store.UnsetPriority(steamID)

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]bool{"ok": true})
}

func (s *Server) handleListPriority(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	// Note: This is a simplified placeholder. A real implementation would
	// need store to expose a method to list all priority IDs.
	priorityIDs := []string{}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]interface{}{"priority_ids": priorityIDs})
}
