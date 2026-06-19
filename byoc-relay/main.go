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
	"encoding/binary"
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

// Service-port hardening. The service port is necessarily reachable by attackers
// (and, on a mixed bridge, by jeopardy containers), so cap concurrent forwards
// and evict idle connections — a thin relay with a small memory limit must not
// be slowloris'd into an OOM that takes the team's whole defense down.
const (
	maxServiceConns  = 256
	serviceIdleLimit = 60 * time.Second
)

// Pre-auth hardening for the control + flag ports. They sit on the shared bridge,
// so cap concurrent UNAUTHENTICATED handshakes and bound the secret line — a peer
// can't OOM the thin relay with a connection storm or a newline-flood (a line with
// no '\n') before the secret check. authLineBudget is far above the ~64-128 hex
// secret; the slot is released as soon as the handshake completes.
const (
	maxAuthConns   = 128
	authLineBudget = 512
)

var authSlots = make(chan struct{}, maxAuthConns)

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
	secret      string // shared with GZCTF; presented on the control + flag ports
	mu          sync.Mutex
	session     *yamux.Session
	flag        []byte // most recent flag, replayed to a freshly connected agent
	flagSeq     uint64 // monotonic; lets the agent ignore a stale/out-of-order flag
	serviceSems chan struct{}
}

// idleConn evicts a connection that goes quiet: every read/write refreshes a
// rolling deadline, so a slowloris peer that stops sending eventually trips the
// deadline and io.Copy unwinds, freeing the goroutine + its slot.
type idleConn struct {
	net.Conn
	idle time.Duration
}

func (c idleConn) Read(p []byte) (int, error) {
	_ = c.Conn.SetReadDeadline(time.Now().Add(c.idle))
	return c.Conn.Read(p)
}

func (c idleConn) Write(p []byte) (int, error) {
	_ = c.Conn.SetWriteDeadline(time.Now().Add(c.idle))
	return c.Conn.Write(p)
}

func runRelay() {
	svcPort := env("GZCTF_BYOC_SVC_PORT", "80")
	ctlPort := env("GZCTF_BYOC_CTL_PORT", "47000")
	flagPort := env("GZCTF_BYOC_FLAG_PORT", "47001")

	// The control + flag ports sit on the shared challenge bridge, which a
	// compromised jeopardy container can also reach — so they are NOT trusted by
	// network position. GZCTF presents this secret (gzctf↔relay only, never the
	// team) as the first line on every connection; we reject anything else.
	r := &relay{
		secret:      os.Getenv("GZCTF_BYOC_SECRET"),
		serviceSems: make(chan struct{}, maxServiceConns),
	}
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
	// Bound concurrent unauthenticated handshakes; drop the excess immediately.
	// The slot covers only the handshake (released on return), not the live tunnel.
	select {
	case authSlots <- struct{}{}:
		defer func() { <-authSlots }()
	default:
		return nil, false
	}

	_ = c.SetReadDeadline(time.Now().Add(10 * time.Second))
	// Bounded buffer: ReadSlice returns ErrBufferFull (→ reject) once the secret
	// line exceeds authLineBudget, so a no-newline flood can't grow memory unbounded
	// (ReadString would chain fragments without limit).
	br := bufio.NewReaderSize(c, authLineBudget)
	line, err := br.ReadSlice('\n')
	_ = c.SetReadDeadline(time.Time{})
	if err != nil {
		return nil, false
	}
	got := strings.TrimRight(string(line), "\r\n")
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
	seq := r.flagSeq
	r.mu.Unlock()

	if len(flag) > 0 {
		r.pushFlag(session, seq, flag)
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
	// Bound concurrent forwards so a flood can't exhaust the relay's memory; drop
	// when full (the checker/attacker retries). Idle connections are evicted via
	// idleConn below, so a slowloris peer can't pin a slot indefinitely.
	select {
	case r.serviceSems <- struct{}{}:
		defer func() { <-r.serviceSems }()
	default:
		return
	}
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
	pipe(idleConn{Conn: c, idle: serviceIdleLimit}, stream)
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
	r.flagSeq++
	r.flag = flag
	seq := r.flagSeq
	session := r.session
	r.mu.Unlock()
	if session != nil {
		r.pushFlag(session, seq, flag)
	}
}

// pushFlag opens an 'F' stream and writes [type][8-byte big-endian seq][flag].
// The monotonic seq lets the agent drop a stale flag that raced a fresh push
// (e.g. a reconnect's replay arriving after the next tick's rotation).
func (r *relay) pushFlag(session *yamux.Session, seq uint64, flag []byte) {
	stream, err := session.OpenStream()
	if err != nil {
		return
	}
	defer stream.Close()
	var hdr [9]byte
	hdr[0] = streamFlag
	binary.BigEndian.PutUint64(hdr[1:], seq)
	if _, err := stream.Write(hdr[:]); err != nil {
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

	// Per-connection flag serializer: monotonic seq + a mutex so two flag streams
	// (e.g. a reconnect replay racing a fresh push) can't let the older flag win
	// the os.Rename. Reset per connection so a relay restart (seq back to 0) still
	// delivers.
	sink := &flagSink{}
	for {
		stream, err := session.AcceptStream()
		if err != nil {
			return fmt.Errorf("accept stream: %w", err)
		}
		go handleAgentStream(stream, service, flagFile, sink)
	}
}

// flagSink serializes flag writes and drops stale (lower-seq) flags.
type flagSink struct {
	mu   sync.Mutex
	last uint64
}

// handleAgentStream dispatches one forwarded stream by its leading type byte.
func handleAgentStream(stream *yamux.Stream, service, flagFile string, sink *flagSink) {
	defer stream.Close()
	var hdr [1]byte
	if _, err := io.ReadFull(stream, hdr[:]); err != nil {
		return
	}
	switch hdr[0] {
	case streamService:
		dialAndPipe(stream, service)
	case streamFlag:
		writeFlag(stream, flagFile, sink)
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

// writeFlag reads [8-byte big-endian seq][flag] and, if seq is newer than any
// flag already applied, writes it atomically to flagFile (temp file + rename).
// The sink's mutex serializes the write+rename, so an older flag that raced a
// newer one can never win the rename.
func writeFlag(stream *yamux.Stream, flagFile string, sink *flagSink) {
	var seqBuf [8]byte
	if _, err := io.ReadFull(stream, seqBuf[:]); err != nil {
		return
	}
	seq := binary.BigEndian.Uint64(seqBuf[:])
	flag, err := io.ReadAll(io.LimitReader(stream, 4096))
	if err != nil || len(flag) == 0 {
		return
	}

	sink.mu.Lock()
	defer sink.mu.Unlock()
	if seq <= sink.last {
		return // a newer flag already landed
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
	sink.last = seq
	log.Printf("flag updated (seq %d, %d bytes)", seq, len(flag))
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
