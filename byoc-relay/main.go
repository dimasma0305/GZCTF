// Command gzctf-byoc is the GZCTF Attack & Defense "bring your own container"
// (BYOC) tunnel. A single static binary runs in one of two modes:
//
//	relay — launched by GZCTF inside the per-team challenge bridge. Its bridge IP
//	        is the team's service endpoint, so the SLA checker and attackers
//	        connect to it exactly as they would a GZCTF-hosted container. It
//	        forwards those connections, over one multiplexed session, to the
//	        team's self-hosted service, and pushes each rotating flag to the agent.
//
//	agent — run by the team next to their own service container. It dials OUTBOUND
//	        (WebSocket, through GZCTF's existing proxy bridge) to the relay's
//	        control port, accepts forwarded streams and connects them to the local
//	        service, and writes each rotating flag it receives to a shared file.
//
// Transport: the agent and relay speak yamux. GZCTF bridges the agent's
// WebSocket to the relay's control TCP port byte-for-byte (the same WS<->TCP
// bridge ProxyController already uses for attack traffic), so the agent runs
// yamux over the WebSocket-as-net.Conn while the relay runs yamux over the raw
// TCP connection; the frames line up transparently. No inbound port, public IP,
// or VPN is needed on the team's side — the agent only makes one outbound HTTPS
// connection.
//
// Stream protocol: every relay->agent stream begins with a single type byte:
//
//	'S' service: relay opened it because a client hit the service port. The agent
//	             dials the local service and pipes raw bytes after the type byte.
//	'F' flag:    relay opened it to deliver a new flag. The bytes after the type
//	             byte (until stream close) are the flag; the agent writes them
//	             atomically to the flag file.
package main

import (
	"bufio"
	"context"
	"crypto/subtle"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/coder/websocket"
	"github.com/hashicorp/yamux"
)

// Stream type bytes (first byte of every relay->agent stream).
const (
	streamService byte = 'S'
	streamFlag    byte = 'F'
)

func main() {
	log.SetFlags(log.LstdFlags | log.Lmsgprefix)
	mode := strings.ToLower(env("GZCTF_BYOC_MODE", ""))
	switch mode {
	case "relay":
		log.SetPrefix("[byoc-relay] ")
		runRelay()
	case "agent":
		log.SetPrefix("[byoc-agent] ")
		runAgent()
	default:
		fmt.Fprintln(os.Stderr, "GZCTF_BYOC_MODE must be 'relay' or 'agent'")
		os.Exit(2)
	}
}

// ---------------------------------------------------------------------------
// relay mode
// ---------------------------------------------------------------------------

// relay holds the single live agent session. The service and flag listeners
// read it under the mutex; a new control connection replaces (and tears down)
// the previous session so a reconnecting agent always wins.
type relay struct {
	secret  string // shared with GZCTF; presented on the control + flag ports
	mu      sync.Mutex
	session *yamux.Session
	flag    []byte // most recent flag, replayed to a freshly connected agent
}

func runRelay() {
	svcPort := env("GZCTF_BYOC_SVC_PORT", "80")
	ctlPort := env("GZCTF_BYOC_CTL_PORT", "47000")
	flagPort := env("GZCTF_BYOC_FLAG_PORT", "47001")

	// The control + flag ports sit on the shared challenge bridge, which a
	// compromised jeopardy container can also reach — so they are NOT trusted by
	// network position. GZCTF presents this secret (gzctf↔relay only, never the
	// team) as the first line on every connection; we reject anything else.
	r := &relay{secret: os.Getenv("GZCTF_BYOC_SECRET")}
	go r.serve(ctlPort, r.handleControl, "control")
	go r.serve(flagPort, r.handleFlagPush, "flag-push")
	r.serve(svcPort, r.handleService, "service") // blocks
}

// bufConn is a net.Conn whose reads come from a buffered reader (which may hold
// bytes already pulled past the secret line), while writes/close go to the raw
// connection. Lets yamux run over the post-handshake byte stream cleanly.
type bufConn struct {
	net.Conn
	r io.Reader
}

func (b *bufConn) Read(p []byte) (int, error) { return b.r.Read(p) }

// authenticate reads the leading secret line and constant-time compares it to
// the relay's secret. Returns a reader positioned at the first post-secret byte
// (preserving anything the bufio reader already buffered), or false on mismatch.
func (r *relay) authenticate(c net.Conn) (*bufio.Reader, bool) {
	_ = c.SetReadDeadline(time.Now().Add(10 * time.Second))
	br := bufio.NewReader(c)
	line, err := br.ReadString('\n')
	_ = c.SetReadDeadline(time.Time{})
	if err != nil {
		return nil, false
	}
	got := strings.TrimRight(line, "\r\n")
	if subtle.ConstantTimeCompare([]byte(got), []byte(r.secret)) != 1 {
		log.Printf("rejected %s: bad secret", c.RemoteAddr())
		return nil, false
	}
	return br, true
}

// serve accepts connections on the given port forever, dispatching each to fn.
func (r *relay) serve(port string, fn func(net.Conn), name string) {
	ln, err := net.Listen("tcp", ":"+port)
	if err != nil {
		log.Fatalf("listen %s (:%s): %v", name, port, err)
	}
	log.Printf("%s listener on :%s", name, port)
	for {
		c, err := ln.Accept()
		if err != nil {
			log.Printf("%s accept: %v", name, err)
			time.Sleep(200 * time.Millisecond)
			continue
		}
		go fn(c)
	}
}

// handleControl wraps a freshly connected agent transport as a yamux server
// session, makes it the live session, and replays the current flag so the team
// is never stuck a full tick without one after a reconnect.
func (r *relay) handleControl(c net.Conn) {
	br, ok := r.authenticate(c)
	if !ok {
		_ = c.Close()
		return
	}
	session, err := yamux.Server(&bufConn{Conn: c, r: br}, yamuxConfig())
	if err != nil {
		log.Printf("control: yamux server: %v", err)
		_ = c.Close()
		return
	}
	log.Printf("agent connected from %s", c.RemoteAddr())

	r.mu.Lock()
	if old := r.session; old != nil {
		_ = old.Close()
	}
	r.session = session
	flag := r.flag
	r.mu.Unlock()

	if len(flag) > 0 {
		r.pushFlag(session, flag)
	}

	// Block until the session dies so we can clear it (and stop accepting
	// service streams against a dead transport).
	<-session.CloseChan()
	r.mu.Lock()
	if r.session == session {
		r.session = nil
	}
	r.mu.Unlock()
	log.Printf("agent disconnected (%s)", c.RemoteAddr())
}

// handleService forwards one inbound service connection (checker or attacker) to
// the agent's local service over a new 'S' stream. With no live agent the
// connection is closed immediately, which the SLA checker reads as "down".
func (r *relay) handleService(c net.Conn) {
	defer c.Close()
	r.mu.Lock()
	session := r.session
	r.mu.Unlock()
	if session == nil {
		return
	}
	stream, err := session.OpenStream()
	if err != nil {
		return
	}
	defer stream.Close()
	if _, err := stream.Write([]byte{streamService}); err != nil {
		return
	}
	pipe(c, stream)
}

// handleFlagPush receives a rotating flag from GZCTF (one flag per connection,
// raw bytes until close) and forwards it to the agent. Reachable only from the
// control plane — challenge-bridge egress isolation blocks other teams.
func (r *relay) handleFlagPush(c net.Conn) {
	defer c.Close()
	br, ok := r.authenticate(c)
	if !ok {
		return
	}
	flag, err := io.ReadAll(io.LimitReader(br, 4096))
	if err != nil || len(flag) == 0 {
		return
	}
	r.mu.Lock()
	r.flag = flag
	session := r.session
	r.mu.Unlock()
	if session != nil {
		r.pushFlag(session, flag)
	}
}

// pushFlag opens an 'F' stream and writes the flag bytes for the agent.
func (r *relay) pushFlag(session *yamux.Session, flag []byte) {
	stream, err := session.OpenStream()
	if err != nil {
		return
	}
	defer stream.Close()
	if _, err := stream.Write([]byte{streamFlag}); err != nil {
		return
	}
	_, _ = stream.Write(flag)
}

// ---------------------------------------------------------------------------
// agent mode
// ---------------------------------------------------------------------------

func runAgent() {
	tunnelURL := mustEnv("GZCTF_BYOC_TUNNEL_URL")    // wss://gzctf/api/Ad/Byoc/Agent/<token>
	service := mustEnv("GZCTF_BYOC_SERVICE")         // host:port of the team's service
	flagFile := env("GZCTF_BYOC_FLAG_FILE", "/flag") // where to write the rotating flag

	for {
		if err := connectOnce(tunnelURL, service, flagFile); err != nil {
			log.Printf("tunnel: %v; reconnecting in 3s", err)
		}
		time.Sleep(3 * time.Second)
	}
}

// connectOnce holds one tunnel session: dial the WebSocket, run a yamux client
// over it, and serve forwarded streams until the connection drops.
func connectOnce(tunnelURL, service, flagFile string) error {
	ctx := context.Background()
	c, _, err := websocket.Dial(ctx, tunnelURL, nil)
	if err != nil {
		return fmt.Errorf("dial: %w", err)
	}
	// 0 = no read limit: yamux frames can exceed the 32 KiB default.
	c.SetReadLimit(-1)
	conn := websocket.NetConn(ctx, c, websocket.MessageBinary)
	defer conn.Close()

	session, err := yamux.Client(conn, yamuxConfig())
	if err != nil {
		return fmt.Errorf("yamux client: %w", err)
	}
	defer session.Close()
	log.Printf("tunnel up to %s, forwarding to %s", tunnelURL, service)

	for {
		stream, err := session.AcceptStream()
		if err != nil {
			return fmt.Errorf("accept stream: %w", err)
		}
		go handleAgentStream(stream, service, flagFile)
	}
}

// handleAgentStream dispatches one forwarded stream by its leading type byte.
func handleAgentStream(stream *yamux.Stream, service, flagFile string) {
	defer stream.Close()
	var hdr [1]byte
	if _, err := io.ReadFull(stream, hdr[:]); err != nil {
		return
	}
	switch hdr[0] {
	case streamService:
		dialAndPipe(stream, service)
	case streamFlag:
		writeFlag(stream, flagFile)
	default:
		log.Printf("unknown stream type %q", hdr[0])
	}
}

// dialAndPipe connects the forwarded stream to the team's local service.
func dialAndPipe(stream *yamux.Stream, service string) {
	c, err := net.DialTimeout("tcp", service, 5*time.Second)
	if err != nil {
		log.Printf("dial service %s: %v", service, err)
		return
	}
	defer c.Close()
	pipe(c, stream)
}

// writeFlag reads the flag from the stream and writes it atomically to flagFile
// (temp file + rename) so the service never observes a half-written flag.
func writeFlag(stream *yamux.Stream, flagFile string) {
	flag, err := io.ReadAll(io.LimitReader(stream, 4096))
	if err != nil || len(flag) == 0 {
		return
	}
	tmp := flagFile + ".tmp"
	if err := os.MkdirAll(filepath.Dir(flagFile), 0o755); err != nil {
		log.Printf("flag dir: %v", err)
		return
	}
	if err := os.WriteFile(tmp, flag, 0o644); err != nil {
		log.Printf("flag write: %v", err)
		return
	}
	if err := os.Rename(tmp, flagFile); err != nil {
		log.Printf("flag rename: %v", err)
		return
	}
	log.Printf("flag updated (%d bytes)", len(flag))
}

// ---------------------------------------------------------------------------
// shared helpers
// ---------------------------------------------------------------------------

// pipe relays bytes bidirectionally between a and b until either side closes.
func pipe(a, b io.ReadWriteCloser) {
	var wg sync.WaitGroup
	wg.Add(2)
	cp := func(dst, src io.ReadWriteCloser) {
		defer wg.Done()
		_, _ = io.Copy(dst, src)
		_ = dst.Close()
		_ = src.Close()
	}
	go cp(a, b)
	go cp(b, a)
	wg.Wait()
}

func yamuxConfig() *yamux.Config {
	cfg := yamux.DefaultConfig()
	cfg.EnableKeepAlive = true
	cfg.KeepAliveInterval = 15 * time.Second
	cfg.ConnectionWriteTimeout = 20 * time.Second
	cfg.LogOutput = io.Discard // route through our own logging instead
	return cfg
}

func env(key, def string) string {
	if v, ok := os.LookupEnv(key); ok && v != "" {
		return v
	}
	return def
}

func mustEnv(key string) string {
	v, ok := os.LookupEnv(key)
	if !ok || v == "" {
		log.Fatalf("%s is required", key)
	}
	return v
}
