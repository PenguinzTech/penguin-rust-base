package rcon

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

var upgrader = websocket.Upgrader{
	CheckOrigin: func(r *http.Request) bool { return true },
}

// testServer starts a WebSocket server that collects received messages.
// Returns the server, a channel of received rconMsg values, and a cleanup func.
func testServer(t *testing.T) (*httptest.Server, chan rconMsg, func()) {
	t.Helper()
	received := make(chan rconMsg, 32)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		conn, err := upgrader.Upgrade(w, r, nil)
		if err != nil {
			return
		}
		defer conn.Close()
		for {
			_, raw, err := conn.ReadMessage()
			if err != nil {
				return
			}
			var msg rconMsg
			if err := json.Unmarshal(raw, &msg); err != nil {
				continue
			}
			received <- msg
		}
	}))
	return srv, received, func() { srv.Close() }
}

// TestNotifier_SendsMessage verifies auth then say command are delivered.
func TestNotifier_SendsMessage(t *testing.T) {
	srv, received, cleanup := testServer(t)
	defer cleanup()

	wsURL := "ws" + strings.TrimPrefix(srv.URL, "http")
	n := NewNotifier(wsURL, "testpass", 8)
	n.Start()
	defer n.Stop()

	n.Notify("test")

	// Expect auth message first (Identifier=1), then command (Identifier=2)
	timeout := time.After(3 * time.Second)
	var msgs []rconMsg
	for len(msgs) < 2 {
		select {
		case msg := <-received:
			msgs = append(msgs, msg)
		case <-timeout:
			t.Fatalf("timeout waiting for messages; got %d: %v", len(msgs), msgs)
		}
	}

	if msgs[0].Identifier != 1 {
		t.Errorf("first message Identifier = %d, want 1", msgs[0].Identifier)
	}
	if msgs[0].Message != "testpass" {
		t.Errorf("auth message = %q, want %q", msgs[0].Message, "testpass")
	}
	if msgs[1].Identifier != 2 {
		t.Errorf("second message Identifier = %d, want 2", msgs[1].Identifier)
	}
	if msgs[1].Message != "say WAF: test" {
		t.Errorf("command message = %q, want %q", msgs[1].Message, "say WAF: test")
	}
}

// TestNotifier_QueueFull verifies Notify() never blocks when queue is full.
func TestNotifier_QueueFull(t *testing.T) {
	// Use an unreachable address so sendOne fails fast and the queue stays full.
	n := NewNotifier("ws://127.0.0.1:1", "pass", 2)
	// Do NOT start the notifier — queue drains only if Start() is called.
	// Fill the queue.
	n.Notify("a")
	n.Notify("b")

	done := make(chan struct{})
	go func() {
		defer close(done)
		// These should be dropped (non-blocking).
		n.Notify("c")
		n.Notify("d")
		n.Notify("e")
	}()

	select {
	case <-done:
		// pass — Notify returned without blocking
	case <-time.After(2 * time.Second):
		t.Error("Notify() blocked on full queue")
	}
}

// TestNotifier_StopsCleanly verifies Stop() returns promptly with no goroutine leak.
func TestNotifier_StopsCleanly(t *testing.T) {
	srv, _, cleanup := testServer(t)
	defer cleanup()

	wsURL := "ws" + strings.TrimPrefix(srv.URL, "http")
	n := NewNotifier(wsURL, "pass", 8)

	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		n.Start()
		n.Stop()
	}()

	stopped := make(chan struct{})
	go func() {
		wg.Wait()
		close(stopped)
	}()

	select {
	case <-stopped:
		// pass
	case <-time.After(3 * time.Second):
		t.Error("Stop() did not return in time — possible goroutine leak")
	}
}

// TestNotifier_ServerUnavailable verifies sendOne to bad addr logs and does not panic.
func TestNotifier_ServerUnavailable(t *testing.T) {
	n := NewNotifier("ws://127.0.0.1:1", "pass", 8)
	n.Start()
	defer n.Stop()

	// Should not panic; sendOne logs the error.
	done := make(chan struct{})
	go func() {
		defer close(done)
		n.Notify("hello")
	}()

	select {
	case <-done:
		// pass
	case <-time.After(5 * time.Second):
		t.Error("Notify() blocked when server unavailable")
	}
	// Give sendOne time to complete (or fail) without panic.
	time.Sleep(200 * time.Millisecond)
}
