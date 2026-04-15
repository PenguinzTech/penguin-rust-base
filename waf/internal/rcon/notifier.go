package rcon

import (
	"encoding/json"
	"fmt"
	"log"
	"time"

	"github.com/gorilla/websocket"
)

type rconMsg struct {
	Identifier int    `json:"Identifier"`
	Message    string `json:"Message"`
	Name       string `json:"Name"`
}

// Notifier sends async WAF alert messages to the Rust RCON console.
// Connects to the upstream loopback RCON port (game server's 28116, not the public proxy port).
type Notifier struct {
	addr     string
	password string
	queue    chan string
	done     chan struct{}
}

// NewNotifier creates a Notifier. Call Start() to begin processing.
// bufSize is the message buffer (default 64 if <= 0).
func NewNotifier(addr, password string, bufSize int) *Notifier {
	if bufSize <= 0 {
		bufSize = 64
	}
	return &Notifier{
		addr:     addr,
		password: password,
		queue:    make(chan string, bufSize),
		done:     make(chan struct{}),
	}
}

// Start launches the background goroutine that drains the queue.
// Returns immediately. Call Stop() to shut down.
func (n *Notifier) Start() {
	go n.run()
}

// Stop signals the notifier to shut down and waits for the goroutine to exit.
func (n *Notifier) Stop() {
	close(n.done)
}

// Notify enqueues a message for delivery. Non-blocking — drops message if queue is full.
func (n *Notifier) Notify(msg string) {
	select {
	case n.queue <- msg:
	default:
		// queue full — drop message
	}
}

func (n *Notifier) run() {
	for {
		select {
		case msg := <-n.queue:
			if err := n.sendOne(msg); err != nil {
				log.Printf("[rcon] sendOne error: %v", err)
			}
		case <-n.done:
			return
		}
	}
}

func (n *Notifier) sendOne(msg string) error {
	dialer := websocket.Dialer{
		HandshakeTimeout: 3 * time.Second,
	}
	conn, _, err := dialer.Dial(n.addr, nil)
	if err != nil {
		return fmt.Errorf("dial %s: %w", n.addr, err)
	}
	defer conn.Close()

	deadline := time.Now().Add(5 * time.Second)
	if err := conn.SetWriteDeadline(deadline); err != nil {
		return fmt.Errorf("set write deadline: %w", err)
	}

	// Authenticate
	auth, _ := json.Marshal(rconMsg{Identifier: 1, Message: n.password, Name: "WebRcon"})
	if err := conn.WriteMessage(websocket.TextMessage, auth); err != nil {
		return fmt.Errorf("write auth: %w", err)
	}

	// Send command
	cmd, _ := json.Marshal(rconMsg{Identifier: 2, Message: "say WAF: " + msg, Name: "WebRcon"})
	if err := conn.WriteMessage(websocket.TextMessage, cmd); err != nil {
		return fmt.Errorf("write command: %w", err)
	}

	return nil
}
