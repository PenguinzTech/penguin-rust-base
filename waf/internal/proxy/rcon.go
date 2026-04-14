package proxy

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"log"
	"net"

	"github.com/penguintechinc/penguin-rust-base/waf/internal/detect"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/metrics"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/state"
)

// RCONProxy proxies TCP WebRCON traffic with authentication failure tracking.
type RCONProxy struct {
	listenAddr   string
	upstreamAddr string
	rconTracker  *detect.RCONTracker
	store        *state.Store
	port         int
}

// NewRCONProxy creates a new RCON proxy.
func NewRCONProxy(listenAddr, upstreamAddr string, port int, tracker *detect.RCONTracker, store *state.Store) *RCONProxy {
	return &RCONProxy{
		listenAddr:   listenAddr,
		upstreamAddr: upstreamAddr,
		rconTracker:  tracker,
		store:        store,
		port:         port,
	}
}

// Start listens on listenAddr and proxies connections to upstreamAddr.
// Tracks RCON authentication failures and blocks IPs accordingly.
func (r *RCONProxy) Start(ctx context.Context) error {
	listener, err := net.Listen("tcp", r.listenAddr)
	if err != nil {
		return fmt.Errorf("listen on %s: %w", r.listenAddr, err)
	}
	defer listener.Close()

	log.Printf("RCON proxy listening on %s, forwarding to %s", r.listenAddr, r.upstreamAddr)

	go func() {
		<-ctx.Done()
		listener.Close()
	}()

	for {
		clientConn, err := listener.Accept()
		if err != nil {
			select {
			case <-ctx.Done():
				return nil
			default:
				log.Printf("accept connection: %v", err)
				continue
			}
		}

		clientAddr := clientConn.RemoteAddr().(*net.TCPAddr)
		clientIP := clientAddr.IP

		// Check if IP is blocked
		if r.store.IsIPBlocked(clientIP) {
			log.Printf("[RCON] Reject blocked IP: %s", clientIP.String())
			clientConn.Close()
			continue
		}

		// Spawn handler goroutine
		go r.handleConnection(clientIP, clientConn)
	}
}

func (r *RCONProxy) handleConnection(clientIP net.IP, clientConn net.Conn) {
	defer clientConn.Close()

	// Dial upstream server
	upstreamConn, err := net.Dial("tcp", r.upstreamAddr)
	if err != nil {
		log.Printf("[RCON] Dial upstream %s: %v", r.upstreamAddr, err)
		return
	}
	defer upstreamConn.Close()

	// Track whether auth succeeded
	authFailed := false

	// Bidirectional copy with auth failure detection
	done := make(chan error, 2)

	// Client → Upstream (request copy)
	go func() {
		_, err := io.Copy(upstreamConn, clientConn)
		done <- err
	}()

	// Upstream → Client (response copy with auth tracking)
	go func() {
		err := r.copyWithAuthTracking(clientConn, upstreamConn, clientIP)
		if err != nil {
			authFailed = true
		}
		done <- err
	}()

	// Wait for either direction to close
	<-done

	// If auth failed, record failure; otherwise reset
	if authFailed {
		r.rconTracker.RecordFailure(clientIP, r.store)
		metrics.RCONAuthFailures.WithLabelValues(clientIP.String()).Inc()
	} else {
		r.rconTracker.Reset(clientIP)
	}
}

// copyWithAuthTracking reads from src and writes to dst, scanning for auth failure messages.
func (r *RCONProxy) copyWithAuthTracking(dst io.Writer, src io.Reader, clientIP net.IP) error {
	buffer := make([]byte, 512)
	authFailureMarker := []byte("Invalid Password")
	authFailed := false

	for {
		n, err := src.Read(buffer)
		if n > 0 {
			// Scan for auth failure message
			if bytes.Contains(buffer[:n], authFailureMarker) {
				authFailed = true
			}

			// Write to destination
			if _, writeErr := dst.Write(buffer[:n]); writeErr != nil {
				return writeErr
			}
		}

		if err != nil {
			if err == io.EOF {
				if authFailed {
					return fmt.Errorf("auth failed")
				}
				return nil
			}
			return err
		}
	}
}
