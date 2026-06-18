package main

import (
	"bytes"
	"io"
	"net"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/hashicorp/yamux"
)

// linkSessions wires a relay-side yamux server to an agent-side yamux client
// over an in-memory pipe (stand-in for GZCTF's WS<->TCP bridge), and runs the
// agent's stream-dispatch loop against the given service address + flag file.
func linkSessions(t *testing.T, service, flagFile string) *yamux.Session {
	t.Helper()
	relayConn, agentConn := net.Pipe()
	server, err := yamux.Server(relayConn, yamuxConfig())
	if err != nil {
		t.Fatalf("yamux server: %v", err)
	}
	client, err := yamux.Client(agentConn, yamuxConfig())
	if err != nil {
		t.Fatalf("yamux client: %v", err)
	}
	t.Cleanup(func() { _ = server.Close(); _ = client.Close() })

	go func() {
		for {
			st, err := client.AcceptStream()
			if err != nil {
				return
			}
			go handleAgentStream(st, service, flagFile)
		}
	}()
	return server
}

// TestServiceStreamForwarding proves a relay 'S' stream reaches the team's local
// service: the relay opens a service stream, the agent dials a local echo server,
// and bytes round-trip.
func TestServiceStreamForwarding(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go func(c net.Conn) { _, _ = io.Copy(c, c); _ = c.Close() }(c)
		}
	}()

	server := linkSessions(t, ln.Addr().String(), filepath.Join(t.TempDir(), "flag"))

	st, err := server.OpenStream()
	if err != nil {
		t.Fatal(err)
	}
	defer st.Close()
	if _, err := st.Write([]byte{streamService}); err != nil {
		t.Fatal(err)
	}
	msg := []byte("ping-123")
	if _, err := st.Write(msg); err != nil {
		t.Fatal(err)
	}
	_ = st.SetReadDeadline(time.Now().Add(3 * time.Second))
	buf := make([]byte, len(msg))
	if _, err := io.ReadFull(st, buf); err != nil {
		t.Fatalf("read echo: %v", err)
	}
	if !bytes.Equal(buf, msg) {
		t.Fatalf("echo mismatch: got %q want %q", buf, msg)
	}
}

// TestFlagStreamWritesFile proves a relay 'F' stream lands the flag atomically in
// the agent's flag file (including creating a missing parent directory).
func TestFlagStreamWritesFile(t *testing.T) {
	flagFile := filepath.Join(t.TempDir(), "nested", "flag")
	server := linkSessions(t, "127.0.0.1:1", flagFile)

	(&relay{}).pushFlag(server, []byte("flag{byoc_works}"))

	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if b, err := os.ReadFile(flagFile); err == nil && string(b) == "flag{byoc_works}" {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatal("flag file was not written within deadline")
}

// TestAuthenticate proves the relay's control/flag ports require GZCTF's secret:
// a good secret is accepted and leaves trailing bytes intact for yamux; a bad
// secret is rejected; an empty configured secret expects an empty leading line.
// This is the boundary that stops a compromised jeopardy container (which shares
// the challenge bridge) from hijacking a relay or griefing its flag.
func TestAuthenticate(t *testing.T) {
	r := &relay{secret: "topsecret"}

	// good secret, with a trailing payload that must survive for yamux
	good, gc := net.Pipe()
	go func() { _, _ = gc.Write([]byte("topsecret\nHELLO")) }()
	br, ok := r.authenticate(good)
	if !ok {
		t.Fatal("good secret was rejected")
	}
	buf := make([]byte, 5)
	if _, err := io.ReadFull(br, buf); err != nil || string(buf) != "HELLO" {
		t.Fatalf("trailing bytes after secret lost: %q (err=%v)", buf, err)
	}
	_ = good.Close()

	// wrong secret is rejected
	bad, bc := net.Pipe()
	go func() { _, _ = bc.Write([]byte("wrongsecret\n")); _ = bc.Close() }()
	if _, ok := r.authenticate(bad); ok {
		t.Fatal("bad secret was accepted")
	}
	_ = bad.Close()

	// empty configured secret (dev) expects an empty leading line
	r2 := &relay{secret: ""}
	e, ec := net.Pipe()
	go func() { _, _ = ec.Write([]byte("\n")) }()
	if _, ok := r2.authenticate(e); !ok {
		t.Fatal("empty secret line rejected for empty configured secret")
	}
	_ = e.Close()
}

// TestServiceStreamNoAgentClosed proves the relay closes inbound service
// connections when no agent is attached (so the SLA checker reads "down").
func TestServiceStreamNoAgentClosed(t *testing.T) {
	r := &relay{} // no session
	a, b := net.Pipe()
	defer b.Close()
	done := make(chan struct{})
	go func() { r.handleService(a); close(done) }()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("handleService did not return/close with no agent")
	}
	// the connection must be closed
	_ = b.SetReadDeadline(time.Now().Add(time.Second))
	if _, err := b.Read(make([]byte, 1)); err == nil {
		t.Fatal("expected closed connection")
	}
}
