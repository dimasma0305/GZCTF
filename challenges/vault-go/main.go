// "KeyVault" — a secret store used as an Attack & Defense target.
//
// GET /secret?token=<t> returns the rotating flag, but only if the token
// check passes. The deliberate VULNERABILITY is that the token is a weak,
// hardcoded constant ("admin"), so any attacker can read the secret. (It runs
// on Alpine — i.e. the container HAS a shell — so the admin file-inspection /
// per-round snapshot features still work against it, unlike pwn-scratch.)
// Defenders should replace the auth with a real, per-team secret while keeping
// the checker (which knows the weak token) green — i.e. they must coordinate a
// patched checker, the realistic A&D defense bind.
package main

import (
	"net/http"
	"os"
	"strings"
)

func env(k, d string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return d
}

func flag() string {
	b, _ := os.ReadFile(env("GZCTF_FLAG_FILE", "/flag"))
	return strings.TrimSpace(string(b))
}

func main() {
	http.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("ok"))
	})
	http.HandleFunc("/secret", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Get("token") != "admin" { // weak hardcoded auth
			w.WriteHeader(http.StatusForbidden)
			w.Write([]byte("forbidden"))
			return
		}
		w.Write([]byte(flag()))
	})
	http.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("KeyVault — /secret?token=<t> ; /health"))
	})
	http.ListenAndServe(":8080", nil)
}
